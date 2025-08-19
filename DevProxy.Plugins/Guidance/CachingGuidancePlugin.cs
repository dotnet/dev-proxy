// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Guidance;

public sealed class CachingGuidancePluginConfiguration
{
    public int CacheThresholdSeconds { get; set; } = 5;
}

public sealed class CachingGuidancePlugin(
    HttpClient httpClient,
    ILogger<CachingGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection configurationSection) :
    BasePlugin<CachingGuidancePluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        configurationSection)
{
    private readonly Dictionary<string, DateTime> _interceptedRequests = [];

    public override string Name => nameof(CachingGuidancePlugin);

    public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestLogAsync));

        ArgumentNullException.ThrowIfNull(args);

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }
        if (args.Request.Method == HttpMethod.Options)
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        var request = args.Request;
        var url = request.RequestUri!.AbsoluteUri;
        var now = DateTime.Now;

        if (!_interceptedRequests.TryGetValue(url, out var value))
        {
            value = now;
            _interceptedRequests.Add(url, value);
            Logger.LogRequest("First request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        var lastIntercepted = value;
        var secondsSinceLastIntercepted = (now - lastIntercepted).TotalSeconds;
        if (secondsSinceLastIntercepted <= Configuration.CacheThresholdSeconds)
        {
            Logger.LogRequest(BuildCacheWarningMessage(request, Configuration.CacheThresholdSeconds, lastIntercepted), MessageType.Warning, args.Request);
        }
        else
        {
            Logger.LogRequest("Request outside of cache window", MessageType.Skipped, args.Request);
        }

        _interceptedRequests[url] = now;

        Logger.LogTrace("Left {Name}", nameof(OnRequestLogAsync));
        return Task.CompletedTask;
    };

    private static string BuildCacheWarningMessage(HttpRequestMessage r, int warningSeconds, DateTime lastIntercepted) =>
        $"Another request to {r.RequestUri!.PathAndQuery} intercepted within {warningSeconds} seconds. Last intercepted at {lastIntercepted}. Consider using cache to avoid calling the API too often.";
}
