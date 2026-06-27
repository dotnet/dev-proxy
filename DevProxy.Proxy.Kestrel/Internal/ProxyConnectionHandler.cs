// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Handles one raw TCP connection: parses the proxy request, decides at <c>CONNECT</c>
/// time whether to terminate TLS (watched + downgradable client → MITM) or relay the
/// encrypted bytes untouched (non-watched host, or h2-only/gRPC client → blind-tunnel),
/// runs the plugin pipeline against the canonical model for intercepted traffic, forwards
/// to the origin, and writes the response back.
///
/// <para>
/// CONNECT decision flow:
/// <code>
///   CONNECT host:port
///        │  write 200 Connection Established
///        ▼
///   peek ClientHello (non-destructive: SNI + ALPN)
///        │
///        ├─ host not watched ............→ blind-tunnel (never decrypt)
///        ├─ ALPN is h2-only (gRPC) ......→ blind-tunnel (can't downgrade)
///        └─ otherwise ..................→ MITM, advertise http/1.1 so h2 clients downgrade
/// </code>
/// </para>
///
/// <para>
/// Slice scope: one request per intercepted connection (<c>Connection: close</c>),
/// plain HTTP + HTTPS-via-CONNECT, mocking short-circuit, selective decrypt + ALPN
/// blind-tunnel. Deferred hardening (tracked): keep-alive, WebSocket relay + inspection,
/// chunked/trailers, body-mode streaming.
/// </para>
/// </summary>
internal sealed class ProxyConnectionHandler(
    CertificateAuthority ca,
    UpstreamForwarder forwarder,
    PluginPipeline pipeline,
    HostWatchList watchList,
    ILogger logger) : ConnectionHandler
{
    private static int _requestCounter;

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var clientStream = new DuplexPipeStream(connection.Transport);

        try
        {
            var head = await Http1RequestReader.ReadHeadAsync(clientStream, ct).ConfigureAwait(false);
            if (head is null)
            {
                return;
            }

            if (string.Equals(head.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(connection, clientStream, head, ct).ConfigureAwait(false);
            }
            else
            {
                // Plain HTTP proxy request: the target is an absolute URL.
                await ExchangeAsync(clientStream, head, head.Target, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ConnectionResetException)
        {
            // Client disconnect / cancellation — normal teardown, not an error.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling proxy connection");
        }
    }

    private async Task HandleConnectAsync(
        ConnectionContext connection, Stream clientStream, ParsedRequestHead connect, CancellationToken ct)
    {
        var (host, portPart) = SplitHostPort(connect.Target);
        var port = portPart ?? 443;

        // Acknowledge the tunnel so the client begins its TLS handshake.
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

        // Non-destructively peek the ClientHello (SNI + ALPN) before deciding whether
        // to terminate TLS. The bytes stay buffered for SslStream / the blind tunnel.
        var hello = await PeekClientHelloAsync(connection.Transport.Input, ct).ConfigureAwait(false);
        var isWatched = watchList.IsWatched(host);
        var isH2Only = hello.Status == TlsClientHello.ParseStatus.Ok && hello.IsH2Only;

        if (!isWatched)
        {
            logger.LogDebug("CONNECT {Host}:{Port} → blind-tunnel (host not watched)", host, port);
            await BlindTunnelAsync(clientStream, host, port, ct).ConfigureAwait(false);
            return;
        }

        if (isH2Only)
        {
            logger.LogDebug("CONNECT {Host}:{Port} → blind-tunnel (h2-only/gRPC, never MITM)", host, port);
            await BlindTunnelAsync(clientStream, host, port, ct).ConfigureAwait(false);
            return;
        }

        logger.LogDebug("CONNECT {Host}:{Port} → MITM (decrypt as http/1.1)", host, port);
        var certificate = ca.GetCertificateForHost(host);
        await using var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                // Advertise http/1.1 only so any h2-capable client that also offers
                // http/1.1 downgrades and we intercept it as HTTP/1.1.
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, ct).ConfigureAwait(false);

        var head = await Http1RequestReader.ReadHeadAsync(tls, ct).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        var url = port == 443
            ? $"https://{host}{head.Target}"
            : $"https://{host}:{port.ToString(CultureInfo.InvariantCulture)}{head.Target}";

        await ExchangeAsync(tls, head, url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays the raw (still-encrypted) byte stream between the client and the origin
    /// without decrypting, for hosts the proxy must not intercept. The peeked
    /// ClientHello bytes remain buffered on <paramref name="clientStream"/> and are
    /// forwarded as the first bytes of the tunnel.
    /// </summary>
    private async Task BlindTunnelAsync(Stream clientStream, string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "Blind-tunnel connect to {Host}:{Port} failed", host, port);
            return;
        }

        await using var origin = tcp.GetStream();

        // Relay both directions until either side closes. When the first direction
        // finishes, cancel the other so it stops touching the streams. The finally
        // awaits both tasks before `origin`/`tcp` are disposed (CA2025: no task may
        // outlive the IDisposable it uses).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
#pragma warning disable CA2025 // The finally below awaits both copies before origin/clientStream are disposed.
        var clientToOrigin = clientStream.CopyToAsync(origin, linked.Token);
        var originToClient = origin.CopyToAsync(clientStream, linked.Token);

        try
        {
            _ = await Task.WhenAny(clientToOrigin, originToClient).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Task.WhenAll(clientToOrigin, originToClient).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // Either peer closed the connection — normal tunnel teardown.
            }
        }
#pragma warning restore CA2025
    }

    /// <summary>
    /// Reads, without consuming, just enough of the buffered TLS ClientHello to extract
    /// SNI + ALPN. <c>AdvanceTo(buffer.Start)</c> marks nothing consumed and nothing
    /// examined, so the very same bytes are returned to the next reader (SslStream or
    /// the blind tunnel). Using <c>examined = End</c> on the terminal branch would
    /// deadlock — the pipe would wait for bytes the client won't send until it sees a
    /// ServerHello that never comes.
    /// </summary>
    private static async Task<TlsClientHello.Result> PeekClientHelloAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            var parsed = TlsClientHello.Parse(buffer);

            if (parsed.Status != TlsClientHello.ParseStatus.NeedMore)
            {
                reader.AdvanceTo(buffer.Start);
                return parsed;
            }

            // Need more bytes: examine everything so the next read waits for additions.
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return new(TlsClientHello.ParseStatus.NeedMore, null, []);
            }
        }
    }

    private async Task ExchangeAsync(Stream clientStream, ParsedRequestHead head, string absoluteUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var requestUri))
        {
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Malformed request target", ct).ConfigureAwait(false);
            return;
        }

        var contentLength = Http1RequestReader.GetContentLength(head.Headers);
        var body = await Http1RequestReader.ReadBodyAsync(clientStream, head.Leftover, contentLength, ct).ConfigureAwait(false);

        var headers = new HeaderCollection();
        foreach (var (name, value) in head.Headers)
        {
            headers.Add(name, value);
        }

        var version = ParseHttpVersion(head.Version);
        var request = new MutableHttpRequest(head.Method, requestUri, version, headers, body);
        var session = new CanonicalProxySession(
            Guid.NewGuid().ToString("n"),
            request,
            processId: null,
            requestId: Interlocked.Increment(ref _requestCounter));

        RequestPhase phase;
        try
        {
            phase = await pipeline.RunRequestAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pipeline.Forget(session.SessionId);
            logger.LogError(ex, "Error running request pipeline");
            await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Plugin pipeline error", ct).ConfigureAwait(false);
            return;
        }

        // Mocked: a plugin produced the response during the request phase. Skip the
        // upstream forward, but still run the response pipeline so reporters/loggers
        // observe the mock and the console formatter flushes its buffered request log.
        if (phase == RequestPhase.Mocked)
        {
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, ct).ConfigureAwait(false);
            return;
        }

        MutableHttpResponse response;
        try
        {
            response = await forwarder.ForwardAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pipeline.Forget(session.SessionId);
            logger.LogError(ex, "Error forwarding to origin {Url}", absoluteUrl);
            await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Upstream request failed", ct).ConfigureAwait(false);
            return;
        }

        if (phase == RequestPhase.Watched)
        {
            session.SetOriginResponse(response);
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, ct).ConfigureAwait(false);
        }
        else
        {
            // NotWatched: pure passthrough, no response-phase plugins.
            await ResponseWriter.WriteAsync(clientStream, response, ct).ConfigureAwait(false);
        }
    }

    private static Version ParseHttpVersion(string token)
    {
        // token like "HTTP/1.1"
        var slash = token.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0 && Version.TryParse(token[(slash + 1)..], out var version))
        {
            return version;
        }
        return HttpVersion.Version11;
    }

    private static async Task WriteErrorAsync(Stream clientStream, HttpStatusCode status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {(int)status} {ReasonPhrase(status)}\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        try
        {
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await clientStream.WriteAsync(body, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Client already gone.
        }
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.BadGateway => "Bad Gateway",
        _ => status.ToString(),
    };

    private static (string Host, int? Port) SplitHostPort(string authority)
    {
        var separator = authority.LastIndexOf(':');
        if (separator > 0 && int.TryParse(authority[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (authority[..separator], port);
        }
        return (authority, null);
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
