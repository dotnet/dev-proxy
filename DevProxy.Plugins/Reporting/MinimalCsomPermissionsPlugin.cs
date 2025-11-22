// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.SharePoint;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporting;

public sealed class MinimalCsomPermissionsPluginConfiguration
{
    public CsomTypesDefinition TypesDefinitions { get; set; } = new();
    public string? TypesFilePath { get; set; }
}

public sealed class MinimalCsomPermissionsPlugin(
    HttpClient httpClient,
    ILogger<MinimalCsomPermissionsPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<MinimalCsomPermissionsPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private CsomTypesDefinitionLoader? _loader;

    public override string Name => nameof(MinimalCsomPermissionsPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("Entered MinimalCsomPermissionsPlugin.InitializeAsync");

        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        if (string.IsNullOrWhiteSpace(Configuration.TypesFilePath))
        {
            Configuration.TypesFilePath = "~appFolder/config/spo-csom-types.json";
        }

        Configuration.TypesFilePath = ProxyUtils.GetFullPath(Configuration.TypesFilePath, ProxyConfiguration.ConfigFile);
        if (!Path.Exists(Configuration.TypesFilePath))
        {
            throw new InvalidOperationException($"TypesFilePath '{Configuration.TypesFilePath}' does not exist.");
        }

        _loader = ActivatorUtilities.CreateInstance<CsomTypesDefinitionLoader>(e.ServiceProvider, Configuration);
        await _loader.InitFileWatcherAsync(cancellationToken);

        Logger.LogTrace("Left MinimalCsomPermissionsPlugin.RegisterAsync");
    }

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        Logger.LogTrace("Entered MinimalCsomPermissionsPlugin.AfterRecordingStopAsync");

        var interceptedRequests = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedRequest &&
                l.Message.StartsWith("POST", StringComparison.OrdinalIgnoreCase) &&
                l.Message.Contains("/_vti_bin/client.svc/ProcessQuery", StringComparison.InvariantCultureIgnoreCase)
            );
        if (!interceptedRequests.Any())
        {
            Logger.LogRequest("No CSOM requests to process", MessageType.Skipped);
            return;
        }

        Logger.LogInformation("Checking if recorded CSOM requests use minimal permissions...");

        var actions = new List<string>();
        var errors = new List<string>();

        foreach (var request in interceptedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Context == null)
            {
                continue;
            }

            if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                Logger.LogDebug("URL not matched: {Url}", request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri);
                continue;
            }

            var requestBody = await request.Context.Session.GetRequestBodyAsString(cancellationToken);
            if (string.IsNullOrEmpty(requestBody))
            {
                continue;
            }

            Logger.LogDebug("Processing request: {Csom}", requestBody);

            var (requestActions, requestErrors) = CsomParser.GetActions(requestBody, Configuration.TypesDefinitions!);

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Actions: {Actions}", string.Join(", ", requestActions));
                Logger.LogDebug("Errors: {Errors}", string.Join(", ", requestErrors));
            }

            actions.AddRange(requestActions);
            errors.AddRange(requestErrors);

            if (requestErrors.Any() && Logger.IsEnabled(LogLevel.Error))
            {
                Logger.LogError(
                    "The following errors occurred while parsing CSOM:{NewLine}{Errors}",
                    Environment.NewLine,
                    string.Join(Environment.NewLine, requestErrors.Select(e => $"- {e}"))
                );
            }
        }

        if (actions.Count == 0)
        {
            Logger.LogInformation("Haven't detected any CSOM actions to analyze.");
            return;
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Detected {Count} CSOM actions:{NewLine}{Actions}",
                actions.Count,
                Environment.NewLine,
                string.Join(Environment.NewLine, actions.Select(a => $"- {a}"))
            );
        }

        var (minimalScopes, scopesErrors) = CsomParser.GetMinimalScopes(actions, AccessType.Delegated, Configuration.TypesDefinitions!);

        if (scopesErrors.Any())
        {
            errors.AddRange(scopesErrors);
            if (Logger.IsEnabled(LogLevel.Error))
            {
                Logger.LogError(
                    "The following errors occurred while getting minimal scopes:{NewLine}{Errors}",
                    Environment.NewLine,
                    string.Join(Environment.NewLine, scopesErrors.Select(e => $"- {e}"))
                );
            }
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("Minimal permissions: {MinimalScopes}", string.Join(", ", minimalScopes));
        }

        var report = new MinimalCsomPermissionsPluginReport()
        {
            Actions = [.. actions],
            MinimalPermissions = [.. minimalScopes],
            Errors = [.. errors]
        };

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }
}
