// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class CachingGuidancePluginConfiguration
{
    public int CacheThresholdSeconds { get; set; } = 5;
}

public class CachingGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(CachingGuidancePlugin);
    private readonly CachingGuidancePluginConfiguration _configuration = new();
    private Dictionary<string, DateTime> _interceptedRequests = [];

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        PluginEvents.BeforeRequest += BeforeRequestAsync;
    }

    private Task BeforeRequestAsync(object? sender, ProxyRequestArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        Request request = e.Session.HttpClient.Request;
        var url = request.RequestUri.AbsoluteUri;
        var now = DateTime.Now;

        if (!_interceptedRequests.TryGetValue(url, out DateTime value))
        {
            value = now;
            _interceptedRequests.Add(url, value);
            Logger.LogRequest("First request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var lastIntercepted = value;
        var secondsSinceLastIntercepted = (now - lastIntercepted).TotalSeconds;
        if (secondsSinceLastIntercepted <= _configuration.CacheThresholdSeconds)
        {
            Logger.LogRequest(BuildCacheWarningMessage(request, _configuration.CacheThresholdSeconds, lastIntercepted), MessageType.Warning, new LoggingContext(e.Session));
        }
        else
        {
            Logger.LogRequest("Request outside of cache window", MessageType.Skipped, new LoggingContext(e.Session));
        }

        _interceptedRequests[url] = now;
        return Task.CompletedTask;
    }

    private static string BuildCacheWarningMessage(Request r, int _warningSeconds, DateTime lastIntercepted) =>
        $"Another request to {r.RequestUri.PathAndQuery} intercepted within {_warningSeconds} seconds. Last intercepted at {lastIntercepted}. Consider using cache to avoid calling the API too often.";
}
