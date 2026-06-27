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
/// A forward-proxy engine built on ASP.NET Core Kestrel — the replacement for the
/// Titanium-based engine. Hosts a raw TCP endpoint (Kestrel's HTTP middleware is
/// bypassed; a forward proxy speaks the CONNECT protocol and owns the byte stream)
/// and runs the Dev Proxy plugin pipeline against the canonical HTTP model.
///
/// <para>
/// Selected via the engine dev-toggle so it can run side-by-side with the Titanium
/// engine during development for golden-output comparison. Not a shipped fallback —
/// it becomes the only engine at cut-over.
/// </para>
/// </summary>
public sealed class KestrelProxyEngine(
    IEnumerable<IPlugin> plugins,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration configuration,
    Dictionary<string, object> globalData,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<KestrelProxyEngine>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ipAddress = string.IsNullOrWhiteSpace(configuration.IPAddress)
            ? IPAddress.Loopback
            : IPAddress.Parse(configuration.IPAddress);
        var port = configuration.Port;

        using var ca = new CertificateAuthority();
        using var httpHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var httpClient = new HttpClient(httpHandler, disposeHandler: false);
        var forwarder = new UpstreamForwarder(httpClient);
        var pipeline = new PluginPipeline(
            plugins,
            urlsToWatch,
            configuration,
            globalData,
            loggerFactory.CreateLogger<KestrelProxyEngine>());
        var handler = new ProxyConnectionHandler(ca, forwarder, pipeline, _logger);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        _ = builder.WebHost.UseKestrelCore();
        _ = builder.Services.AddSingleton(handler);
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(ipAddress, port, listen => listen.UseConnectionHandler<ProxyConnectionHandler>()));

        await using var app = builder.Build();

        await app.StartAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Dev Proxy (Kestrel engine) listening on {Address}:{Port}",
            ipAddress.ToString(),
            port.ToString(CultureInfo.InvariantCulture));

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
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
