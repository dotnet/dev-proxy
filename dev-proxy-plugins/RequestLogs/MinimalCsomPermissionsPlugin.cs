// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.RequestLogs;

public class MinimalCsomPermissionsPluginReport
{
    public required string[] Actions { get; init; }
    public required string[] MinimalPermissions { get; init; }
    public required string[] Errors { get; init; }
}

public class MinimalCsomPermissionsPluginConfiguration
{
    public CsomTypesDefinition TypesDefinitions { get; set; } = new();
    public string? TypesFilePath { get; set; }
}

public class MinimalCsomPermissionsPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private readonly MinimalCsomPermissionsPluginConfiguration _configuration = new();
    private CsomTypesDefinitionLoader? _loader = null;
    public override string Name => nameof(MinimalCsomPermissionsPlugin);
    private IProxyConfiguration? _proxyConfiguration;

    public override async Task RegisterAsync()
    {
        Logger.LogTrace("Entered MinimalCsomPermissionsPlugin.RegisterAsync");

        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        _proxyConfiguration = Context.Configuration;

        if (string.IsNullOrWhiteSpace(_configuration.TypesFilePath))
        {
            _configuration.TypesFilePath = "~appFolder/config/spo-csom-types.json";
        }

        _configuration.TypesFilePath = Path.GetFullPath(
            ProxyUtils.ReplacePathTokens(_configuration.TypesFilePath),
            Path.GetDirectoryName(_proxyConfiguration?.ConfigFile ?? string.Empty) ?? string.Empty);
        if (!Path.Exists(_configuration.TypesFilePath))
        {
            throw new InvalidOperationException($"TypesFilePath '{_configuration.TypesFilePath}' does not exist.");
        }

        _loader = new CsomTypesDefinitionLoader(Logger, _configuration, Context.Configuration.ValidateSchemas);
        PluginEvents.OptionsLoaded += OnOptionsLoaded;

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;

        Logger.LogTrace("Left MinimalCsomPermissionsPlugin.RegisterAsync");
    }

    private void OnOptionsLoaded(object? sender, OptionsLoadedArgs e)
    {
        _loader?.InitFileWatcher();
    }

#pragma warning disable CS1998
    private async Task AfterRecordingStopAsync(object sender, RecordingArgs e)
#pragma warning restore CS1998
    {
        Logger.LogTrace("Entered MinimalCsomPermissionsPlugin.AfterRecordingStopAsync");

        var interceptedRequests = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedRequest &&
                l.Message.StartsWith("POST") &&
                l.Message.Contains(".sharepoint.com", StringComparison.InvariantCultureIgnoreCase) &&
                l.Message.Contains("/_vti_bin/client.svc/ProcessQuery", StringComparison.InvariantCultureIgnoreCase)
            );
        if (!interceptedRequests.Any())
        {
            Logger.LogDebug("No CSOM requests to process");
            return;
        }

        Logger.LogInformation("Checking if recorded CSOM requests use minimal permissions...");

        var actions = new List<string>();
        var errors = new List<string>();

        foreach (var request in interceptedRequests)
        {
            if (request.Context == null)
            {
                continue;
            }

            var requestBody = await request.Context.Session.GetRequestBodyAsString();
            if (string.IsNullOrEmpty(requestBody))
            {
                continue;
            }

            Logger.LogDebug("Processing request: {Csom}", requestBody);

            var (requestActions, requestErrors) = CsomParser.GetActions(requestBody, _configuration.TypesDefinitions!);

            Logger.LogDebug("Actions: {Actions}", string.Join(", ", requestActions));
            Logger.LogDebug("Errors: {Errors}", string.Join(", ", requestErrors));

            actions.AddRange(requestActions);
            errors.AddRange(requestErrors);

            if (requestErrors.Any())
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

        Logger.LogInformation(
            "Detected {Count} CSOM actions:{NewLine}{Actions}",
            actions.Count,
            Environment.NewLine,
            string.Join(Environment.NewLine, actions.Select(a => $"- {a}"))
        );

        var (minimalScopes, scopesErrors) = CsomParser.GetMinimalScopes(actions, AccessType.Delegated, _configuration.TypesDefinitions!);
        
        if (scopesErrors.Length > 0)
        {
            errors.AddRange(scopesErrors);
            Logger.LogError(
                "The following errors occurred while getting minimal scopes:{NewLine}{Errors}",
                Environment.NewLine,
                string.Join(Environment.NewLine, scopesErrors.Select(e => $"- {e}"))
            );
        }

        Logger.LogInformation("Minimal permissions: {MinimalScopes}", string.Join(", ", minimalScopes));

        var report = new MinimalCsomPermissionsPluginReport()
        {
            Actions = [.. actions],
            MinimalPermissions = [.. minimalScopes],
            Errors = [.. errors]
        };

        StoreReport(report, e);

        Logger.LogTrace("Left MinimalCsomPermissionsPlugin.AfterRecordingStopAsync");
    }
}
