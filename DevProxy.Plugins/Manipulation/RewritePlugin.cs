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

public sealed class RewriteRule
{
    public string? Url { get; set; }
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

public sealed class RewritePlugin(
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
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (Configuration.Rewrites is null ||
            !Configuration.Rewrites.Any())
        {
            Logger.LogRequest("No rewrites configured", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;

        foreach (var rewrite in Configuration.Rewrites)
        {
            if (string.IsNullOrEmpty(rewrite.In?.Url) ||
                string.IsNullOrEmpty(rewrite.Out?.Url))
            {
                continue;
            }

            var newUrl = Regex.Replace(request.Url, rewrite.In.Url, rewrite.Out.Url, RegexOptions.IgnoreCase);

            if (request.Url.Equals(newUrl, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogRequest($"{rewrite.In?.Url}", MessageType.Skipped, new LoggingContext(e.Session));
            }
            else
            {
                Logger.LogRequest($"{rewrite.In?.Url} > {newUrl}", MessageType.Processed, new LoggingContext(e.Session));
                request.Url = newUrl;
            }
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }
}