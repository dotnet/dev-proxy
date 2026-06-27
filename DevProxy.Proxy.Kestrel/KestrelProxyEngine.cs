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
    ISystemProxyManager? systemProxyManager = null) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<KestrelProxyEngine>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
        var processFilter = new ProcessFilter(configuration.WatchPids, configuration.WatchProcessNames);
        var pipeline = new PluginPipeline(
            plugins,
            urlsToWatch,
            configuration,
            globalData,
            loggerFactory.CreateLogger<KestrelProxyEngine>());
        var handler = new ProxyConnectionHandler(ca, forwarder, pipeline, watchList, processFilter, _logger);

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

        await app.StartAsync(stoppingToken).ConfigureAwait(false);
        // When --port 0 is used the OS assigns a free port; Kestrel rewrites the
        // ListenOptions endpoint to the bound port after StartAsync, so log THAT
        // (not the configured 0) so the user can actually connect.
        var boundPort = listenOptions?.IPEndPoint?.Port ?? port;
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

            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
