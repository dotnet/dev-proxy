// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel;

/// <summary>
/// A forward-proxy engine built on ASP.NET Core Kestrel — Dev Proxy's HTTP(S)
/// interception engine. Hosts a raw TCP endpoint (Kestrel's HTTP middleware is
/// bypassed; a forward proxy speaks the CONNECT protocol and owns the byte stream)
/// and runs the Dev Proxy plugin pipeline against the canonical HTTP model.
/// </summary>
public sealed class KestrelProxyEngine(
    CertificateAuthority certificateAuthority,
    IEnumerable<IPlugin> plugins,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration configuration,
    Dictionary<string, object> globalData,
    ILoggerFactory loggerFactory,
    IRootCertificateTrust? rootCertificateTrust = null,
    ISystemProxyManager? systemProxyManager = null,
    IHostApplicationLifetime? hostApplicationLifetime = null) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<KestrelProxyEngine>();

    /// <summary>
    /// True when the engine stopped the host because the proxy port was already in use.
    /// </summary>
    public bool PortInUse { get; private set; }

    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes with true once the proxy is listening, or with false if it
    /// stopped before it could start listening (for example, port in use).
    /// </summary>
    public Task<bool> Started => _started.Task;

    private static bool IsAddressInUse(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException)
            {
                return true;
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _started.TrySetResult(false);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var ipAddress = string.IsNullOrWhiteSpace(configuration.IPAddress)
            ? IPAddress.Loopback
            : IPAddress.Parse(configuration.IPAddress);
        var port = configuration.Port;

        // The certificate authority is owned by DI (shared with the cert command, the
        // Entra mock plugin, and the cert-download API) — do NOT dispose it here.
        var ca = certificateAuthority;
        rootCertificateTrust?.EnsureTrusted(ca.RootCertificate);
        using var httpHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var httpClient = new HttpClient(httpHandler, disposeHandler: false);
        var forwarder = new UpstreamForwarder(httpClient);
        var watchList = HostWatchList.FromUrls(urlsToWatch);
        var processFilter = new ProcessFilter(
            configuration.WatchPids,
            configuration.WatchProcessNames,
            configuration is IProcessTreeProxyConfiguration processTreeConfiguration &&
            processTreeConfiguration.WatchProcessTree);
        var pipeline = new PluginPipeline(
            plugins,
            urlsToWatch,
            configuration,
            globalData,
            loggerFactory.CreateLogger<KestrelProxyEngine>());
        // --timeout / timeoutSeconds: stop Dev Proxy after a period without intercepted
        // requests. Each incoming proxy request resets the countdown.
        using var inactivityTimer = configuration.TimeoutSeconds is { } timeoutSeconds && hostApplicationLifetime is not null
            ? new InactivityTimer(timeoutSeconds, () =>
            {
                _logger.LogInformation(
                    "No requests received in {TimeoutSeconds} seconds. Stopping Dev Proxy...",
                    timeoutSeconds.ToString(CultureInfo.InvariantCulture));
                hostApplicationLifetime.StopApplication();
            })
            : null;
        var handler = new ProxyConnectionHandler(
            ca, forwarder, pipeline, watchList, processFilter, _logger,
            inactivityTimer is null ? null : inactivityTimer.Reset);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        _ = builder.WebHost.UseKestrelCore();
        _ = builder.Services.AddSingleton(handler);
        Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions? listenOptions = null;
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(ipAddress, port, listen =>
            {
                listen.UseConnectionHandler<ProxyConnectionHandler>();
                listenOptions = listen;
            }));

        await using var app = builder.Build();

        try
        {
            await app.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (hostApplicationLifetime is not null && IsAddressInUse(ex))
        {
            // Let the host report a friendly message instead of crashing with a stack trace.
            PortInUse = true;
            _logger.LogDebug(ex, "Failed to bind the proxy to {Address}:{Port}", ipAddress, port);
            // Stopping while the host is still starting surfaces as a cancellation
            // instead of a clean shutdown, so wait until it has started.
            _ = hostApplicationLifetime.ApplicationStarted.Register(hostApplicationLifetime.StopApplication);
            return;
        }
        inactivityTimer?.Reset();
        // When --port 0 is used the OS assigns a free port; Kestrel rewrites the
        // ListenOptions endpoint to the bound port after StartAsync, so log THAT
        // (not the configured 0) so the user can actually connect.
        var boundPort = listenOptions?.IPEndPoint?.Port ?? port;
        // Publish the actually-bound port back to the shared configuration so the
        // host can persist it in the daemon state file (resolves --port 0 to the
        // OS-assigned port). This is the channel the host's readiness check,
        // `devproxy stop`, and `devproxy status` rely on.
        configuration.Port = boundPort;
        _logger.LogInformation(
            "Dev Proxy (Kestrel engine) listening on {Address}:{Port}",
            ipAddress.ToString(),
            boundPort.ToString(CultureInfo.InvariantCulture));

        var systemProxyEnabled = false;
        if (configuration.AsSystemProxy && systemProxyManager is not null)
        {
            // Register with the OS using the actually-bound port (matters for --port 0).
            systemProxyManager.Enable(configuration.IPAddress, boundPort);
            systemProxyEnabled = true;
        }

        _ = _started.TrySetResult(true);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (systemProxyEnabled)
            {
                systemProxyManager!.Disable();
            }

            // Bound the graceful drain so lingering client keep-alive connections (or an
            // idle CONNECT tunnel) can't stall process shutdown — without this the proxy
            // exits cleanly when idle but takes the full Kestrel drain after real traffic,
            // which makes `devproxy stop` falsely report "did not stop in time". After the
            // grace window Kestrel force-closes any remaining connections.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Drain window elapsed; remaining connections were force-closed.
            }
        }
    }
}
