// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
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

    // HTTP: Add latency before request is forwarded
    public override async Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        await ApplyDelayAsync("request", new LoggingContext(e.Session), cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    // Stdio: Add latency before stdin is forwarded to child
    public override async Task BeforeStdinAsync(StdioRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeStdinAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.ShouldExecute())
        {
            return;
        }

        await ApplyDelayAsync("stdin", new StdioLoggingContext(e.Session, StdioMessageDirection.Stdin), cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(BeforeStdinAsync));
    }

    // Stdio: Add latency after stdout is received from child
    public override async Task AfterStdoutAsync(StdioResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterStdoutAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (e.ResponseState.HasBeenSet)
        {
            return;
        }

        await ApplyDelayAsync("stdout", new StdioLoggingContext(e.Session, StdioMessageDirection.Stdout), cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(AfterStdoutAsync));
    }

    // Stdio: Add latency after stderr is received from child
    public override async Task AfterStderrAsync(StdioResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterStderrAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (e.ResponseState.HasBeenSet)
        {
            return;
        }

        await ApplyDelayAsync("stderr", new StdioLoggingContext(e.Session, StdioMessageDirection.Stderr), cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(AfterStderrAsync));
    }

    private async Task ApplyDelayAsync(string context, LoggingContext loggingContext, CancellationToken cancellationToken)
    {
        var delay = _random.Next(Configuration.MinMs, Configuration.MaxMs);
        Logger.LogRequest($"Delaying {context} for {delay}ms", MessageType.Chaos, loggingContext);
        await Task.Delay(delay, cancellationToken);
    }

    private async Task ApplyDelayAsync(string context, StdioLoggingContext loggingContext, CancellationToken cancellationToken)
    {
        var delay = _random.Next(Configuration.MinMs, Configuration.MaxMs);
        Logger.LogRequest($"Delaying {context} for {delay}ms", MessageType.Chaos, loggingContext);
        await Task.Delay(delay, cancellationToken);
    }
}
