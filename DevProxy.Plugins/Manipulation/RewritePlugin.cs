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

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
            return Task.FromResult(PluginResponse.Continue());
        }

        if (Configuration.Rewrites is null ||
            !Configuration.Rewrites.Any())
        {
            Logger.LogRequest("No rewrites configured", MessageType.Skipped, args.Request, args.RequestId);
            return Task.FromResult(PluginResponse.Continue());
        }

        var originalUrl = args.Request.RequestUri?.ToString() ?? string.Empty;
        var newUrl = originalUrl;
        var wasRewritten = false;

        foreach (var rewrite in Configuration.Rewrites)
        {
            if (string.IsNullOrEmpty(rewrite.In?.Url) ||
                string.IsNullOrEmpty(rewrite.Out?.Url))
            {
                continue;
            }

            var rewrittenUrl = Regex.Replace(newUrl, rewrite.In.Url, rewrite.Out.Url, RegexOptions.IgnoreCase);

            if (newUrl.Equals(rewrittenUrl, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogRequest($"{rewrite.In?.Url}", MessageType.Skipped, args.Request, args.RequestId);
            }
            else
            {
                Logger.LogRequest($"{rewrite.In?.Url} > {rewrittenUrl}", MessageType.Processed, args.Request, args.RequestId);
                newUrl = rewrittenUrl;
                wasRewritten = true;
            }
        }

        if (wasRewritten && Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri))
        {
            var newRequest = new HttpRequestMessage(args.Request.Method, newUri);

            // Copy headers
            foreach (var header in args.Request.Headers)
            {
                _ = newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Copy content and content headers
            if (args.Request.Content != null)
            {
                newRequest.Content = args.Request.Content;
            }

            Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
            return Task.FromResult(PluginResponse.Continue(newRequest));
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return Task.FromResult(PluginResponse.Continue());
    };
}