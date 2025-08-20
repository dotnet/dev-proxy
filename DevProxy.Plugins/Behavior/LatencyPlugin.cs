// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Behavior;

public sealed class LatencyConfiguration
{
    public int MinMs { get; set; }
    public int MaxMs { get; set; } = 5000;
}

public sealed class LatencyPlugin(
    HttpClient httpClient,
    ILogger<LatencyPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<LatencyConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly Random _random = new();

    public override string Name => nameof(LatencyPlugin);

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
            return PluginResponse.Continue();
        }

        var delay = _random.Next(Configuration.MinMs, Configuration.MaxMs);
        Logger.LogRequest($"Delaying request for {delay}ms", MessageType.Chaos, args.Request, args.RequestId);
        await Task.Delay(delay, cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return PluginResponse.Continue();
    };
}
