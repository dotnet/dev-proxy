// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy.Kestrel;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Boots a <see cref="KestrelProxyEngine"/> on a free localhost port, watching the
/// supplied host, and hands back an <see cref="HttpClient"/> wired to route through it.
///
/// <code>
///   test HttpClient ──(absolute-form GET http://origin/..)──▶ Kestrel engine ──▶ FakeOrigin
/// </code>
///
/// No OS trust / no system-proxy registration (both injected as null), so the harness
/// never touches the machine. HTTPS MITM rows additionally trust the engine root via a
/// per-client server-certificate callback (see <see cref="CreateHttpClient"/>).
/// </summary>
internal sealed class KestrelProxyHarness : IAsyncDisposable
{
    private readonly KestrelProxyEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    private KestrelProxyHarness(KestrelProxyEngine engine, int port)
    {
        _engine = engine;
        Port = port;
    }

    public static async Task<KestrelProxyHarness> StartAsync(
        string watchedHost,
        IEnumerable<IPlugin>? plugins = null)
    {
        var port = NetUtil.GetFreePort();
        var configuration = new TestProxyConfiguration
        {
            Port = port,
            IPAddress = "127.0.0.1",
            AsSystemProxy = false,
        };

        // Watch http(s)://<host>/* so the engine MITMs/inspects the origin.
        var escapedHost = Regex.Escape(watchedHost);
        var urlsToWatch = new HashSet<UrlToWatch>
        {
            new(new Regex(
                $"^https?://{escapedHost}/.*$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        };

        var engine = new KestrelProxyEngine(
            CertificateAuthority.CreateDefault(),
            plugins ?? [],
            urlsToWatch,
            configuration,
            [],
            NullLoggerFactory.Instance);

        var harness = new KestrelProxyHarness(engine, port);
        await engine.StartAsync(harness._cts.Token).ConfigureAwait(false);
        await harness.WaitUntilListeningAsync().ConfigureAwait(false);
        return harness;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> that routes through the proxy. When
    /// <paramref name="trustAllServerCerts"/> is set (HTTPS rows) the client accepts
    /// the engine's MITM leaf without an OS trust store.
    /// </summary>
    public HttpClient CreateHttpClient(bool trustAllServerCerts = false)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}"),
            UseProxy = true,
            AllowAutoRedirect = false,
        };
        if (trustAllServerCerts)
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private async Task WaitUntilListeningAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Kestrel proxy did not start listening on port {Port.ToString(CultureInfo.InvariantCulture)}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _engine.Dispose();
        _cts.Dispose();
    }
}
