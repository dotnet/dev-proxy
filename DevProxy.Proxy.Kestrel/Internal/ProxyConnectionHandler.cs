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
/// Scope: keep-alive HTTP/1.1 (multiple requests per intercepted connection) for
/// plain HTTP + HTTPS-via-CONNECT, mocking short-circuit, selective decrypt + ALPN
/// blind-tunnel, and transparent WebSocket relay (handshake replayed, frames spliced
/// opaque — see <see cref="WebSocketRelay"/>). Deferred hardening (tracked):
/// WebSocket frame inspection/mocking (plan §7), chunked/trailers +
/// <c>Expect: 100-continue</c> (such requests fall back to <c>Connection: close</c>),
/// body-mode streaming.
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
    private readonly WebSocketRelay _webSocketRelay = new(logger);

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var clientStream = new DuplexPipeStream(connection.Transport);
        var reader = new Http1ConnectionReader(clientStream);

        try
        {
            var head = await reader.ReadHeadAsync(ct).ConfigureAwait(false);
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
                // Plain HTTP proxy request: the target is an absolute URL. Serve this
                // and any subsequent keep-alive requests on the same connection.
                await ServeConnectionAsync(reader, clientStream, head, httpsHost: null, httpsPort: 0, ct)
                    .ConfigureAwait(false);
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

        var tlsReader = new Http1ConnectionReader(tls);
        var head = await tlsReader.ReadHeadAsync(ct).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        await ServeConnectionAsync(tlsReader, tls, head, host, port, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the first request and then every subsequent keep-alive request on the
    /// same (plain or decrypted) connection. Each iteration gets a fresh request id
    /// and a fresh <see cref="CanonicalProxySession"/>, so no per-request plugin state
    /// leaks between pipelined/keep-alive requests on the connection.
    /// </summary>
    /// <param name="httpsHost">Non-null for a decrypted CONNECT tunnel; null for plain HTTP.</param>
    private async Task ServeConnectionAsync(
        Http1ConnectionReader reader,
        Stream clientStream,
        ParsedRequestHead firstHead,
        string? httpsHost,
        int httpsPort,
        CancellationToken ct)
    {
        var head = firstHead;
        while (head is not null)
        {
            var url = httpsHost is null
                ? head.Target // plain HTTP proxy request: absolute-form target
                : httpsPort == 443
                    ? $"https://{httpsHost}{head.Target}"
                    : $"https://{httpsHost}:{httpsPort.ToString(CultureInfo.InvariantCulture)}{head.Target}";

            var keepAlive = await ExchangeAsync(reader, clientStream, head, url, ct).ConfigureAwait(false);
            if (!keepAlive)
            {
                break;
            }

            head = await reader.ReadHeadAsync(ct).ConfigureAwait(false);
        }
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

        // Relay both directions (still-encrypted bytes) until either side closes.
        await StreamRelay.RelayBidirectionalAsync(clientStream, origin, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Runs one request/response exchange and returns whether the connection may be
    /// kept alive for a following request. Always consumes the request body (even on
    /// mock/error) so the reader is positioned at the next request when keep-alive
    /// continues.
    /// </summary>
    private async Task<bool> ExchangeAsync(
        Http1ConnectionReader reader, Stream clientStream, ParsedRequestHead head, string absoluteUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var requestUri))
        {
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Malformed request target", ct).ConfigureAwait(false);
            return false;
        }

        var contentLength = Http1RequestReader.GetContentLength(head.Headers);
        var body = await reader.ReadBodyAsync(contentLength, ct).ConfigureAwait(false);
        var keepAlive = ShouldKeepAlive(head);

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
            return false;
        }

        // Mocked: a plugin produced the response during the request phase. Skip the
        // upstream forward, but still run the response pipeline so reporters/loggers
        // observe the mock and the console formatter flushes its buffered request log.
        if (phase == RequestPhase.Mocked)
        {
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, keepAlive, ct).ConfigureAwait(false);
            return keepAlive;
        }

        // WebSocket upgrade: HttpClient can't carry a 101, so replay the handshake on a
        // raw socket and splice frames. The relay BLOCKS in the splice until the socket
        // closes, so the response pipeline runs inside the handshake callback (before the
        // splice) — that way a watched request is logged and reporters observe it
        // immediately, not when the WebSocket eventually closes. Either way the
        // connection is consumed (no keep-alive after an upgrade).
        if (request.IsWebSocketRequest)
        {
            var handshakeObserved = false;
            async Task OnHandshakeAsync(MutableHttpResponse handshakeResponse)
            {
                handshakeObserved = true;
                if (phase == RequestPhase.Watched)
                {
                    session.SetOriginResponse(handshakeResponse);
                    await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
                }
            }

            try
            {
                await _webSocketRelay.RelayAsync(clientStream, request, requestUri, OnHandshakeAsync, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error relaying WebSocket to {Url}", absoluteUrl);
            }

            // The relay never reached a handshake response (origin connect failed or it
            // closed early). Flush the buffered request log for a watched session so its
            // lines don't linger in the console formatter; otherwise just drop state.
            if (!handshakeObserved)
            {
                if (phase == RequestPhase.Watched)
                {
                    session.SetOriginResponse(new MutableHttpResponse(
                        HttpStatusCode.BadGateway, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty));
                    await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
                }
                else
                {
                    pipeline.Forget(session.SessionId);
                }
            }
            return false;
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
            return false;
        }

        if (phase == RequestPhase.Watched)
        {
            session.SetOriginResponse(response);
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, keepAlive, ct).ConfigureAwait(false);
        }
        else
        {
            // NotWatched: pure passthrough, no response-phase plugins.
            await ResponseWriter.WriteAsync(clientStream, response, keepAlive, ct).ConfigureAwait(false);
        }

        return keepAlive;
    }

    /// <summary>
    /// Decides whether the connection may serve another request after this one.
    /// Persistent by default on HTTP/1.1 (RFC 9112 §9.3), opt-in on HTTP/1.0. We
    /// refuse keep-alive for requests whose body we cannot yet frame for the next
    /// message — a chunked (<c>Transfer-Encoding</c>) body or <c>Expect: 100-continue</c>
    /// — so an unread/misframed body never corrupts a subsequent request.
    /// </summary>
    internal static bool ShouldKeepAlive(ParsedRequestHead head)
    {
        string? connection = null;
        var hasUnframableBody = false;
        foreach (var (name, value) in head.Headers)
        {
            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                connection = value;
            }
            else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase))
            {
                hasUnframableBody = true;
            }
        }

        if (hasUnframableBody)
        {
            return false;
        }

        var isHttp10 = head.Version.EndsWith("1.0", StringComparison.Ordinal);
        if (connection is not null)
        {
            if (connection.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (isHttp10 && connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !isHttp10;
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
