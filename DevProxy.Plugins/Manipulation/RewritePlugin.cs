// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Manipulation;

public sealed class RewriteHeader
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

public sealed class RewriteRule
{
    public string? Url { get; set; }
    public IEnumerable<RewriteHeader>? Headers { get; set; }
}

public sealed class RequestRewrite
{
    public RewriteRule? In { get; set; }
    public RewriteRule? Out { get; set; }
}

public sealed class RewritePluginConfiguration
{
    public IEnumerable<RequestRewrite> Rewrites { get; set; } = [];
    public string RewritesFile { get; set; } = "rewrites.json";
}

public sealed partial class RewritePlugin(
    HttpClient httpClient,
    ILogger<RewritePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<RewritePluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;
    private RewritesLoader? _loader;

    public override string Name => nameof(RewritePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        Configuration.RewritesFile = ProxyUtils.GetFullPath(Configuration.RewritesFile, _proxyConfiguration.ConfigFile);

        _loader = ActivatorUtilities.CreateInstance<RewritesLoader>(e.ServiceProvider, Configuration);
        await _loader.InitFileWatcherAsync(cancellationToken);
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        if (Configuration.Rewrites is null ||
            !Configuration.Rewrites.Any())
        {
            Logger.LogRequest("No rewrites configured", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        var request = e.ProxySession.Request;

        foreach (var rewrite in Configuration.Rewrites)
        {
            if (string.IsNullOrEmpty(rewrite.In?.Url) || rewrite.Out is null)
            {
                continue;
            }

            var headers = rewrite.Out.Headers?
                .Where(h => h is not null && !string.IsNullOrEmpty(h.Name) && h.Value is not null)
                .ToArray() ?? [];
            if (string.IsNullOrEmpty(rewrite.Out.Url) && headers.Length == 0)
            {
                continue;
            }

            // Match against the URL before this rule rewrites it, so that
            // header rewrites apply to the same requests as the URL rewrite.
            var urlMatches = Regex.IsMatch(request.Url, rewrite.In.Url, RegexOptions.IgnoreCase);

            var outUrl = rewrite.Out.Url;
            if (urlMatches)
            {
                var missingVariables = new HashSet<string>(StringComparer.Ordinal);
                outUrl = ExpandEnvironmentVariables(outUrl, missingVariables);
                headers = [.. headers.Select(h => new RewriteHeader
                {
                    Name = h.Name,
                    Value = ExpandEnvironmentVariables(h.Value, missingVariables)
                })];

                if (missingVariables.Count > 0)
                {
                    Logger.LogError(
                        "Skipping rewrite {InUrl} for {RequestUrl}. The following environment variables aren't set: {MissingVariables}",
                        rewrite.In.Url,
                        request.Url,
                        string.Join(", ", missingVariables));
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(outUrl))
            {
                var newUrl = Regex.Replace(request.Url, rewrite.In.Url, outUrl, RegexOptions.IgnoreCase);

                if (request.Url.Equals(newUrl, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogRequest($"{rewrite.In.Url}", MessageType.Skipped, new LoggingContext(e.ProxySession));
                }
                else
                {
                    Logger.LogRequest($"{rewrite.In.Url} > {newUrl}", MessageType.Processed, new LoggingContext(e.ProxySession));
                    request.Url = newUrl;
                }
            }

            if (headers.Length == 0)
            {
                continue;
            }

            if (!urlMatches)
            {
                Logger.LogRequest($"{rewrite.In.Url} (headers)", MessageType.Skipped, new LoggingContext(e.ProxySession));
                continue;
            }

            foreach (var header in headers)
            {
                request.Headers.Replace(header.Name!, header.Value!);
                Logger.LogRequest($"{rewrite.In.Url} > header {header.Name}", MessageType.Processed, new LoggingContext(e.ProxySession));
            }
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    private static string? ExpandEnvironmentVariables(string? value, HashSet<string> missingVariables)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return EnvironmentVariableTokenRegex().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            var envValue = Environment.GetEnvironmentVariable(name);
            if (envValue is null)
            {
                _ = missingVariables.Add(name);
                return match.Value;
            }
            return envValue;
        });
    }

    [GeneratedRegex(@"\$\{env:(?<name>[^}]+)\}")]
    private static partial Regex EnvironmentVariableTokenRegex();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }
}