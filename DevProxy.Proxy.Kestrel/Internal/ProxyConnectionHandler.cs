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
///        ├─ process not watched ........→ blind-tunnel (--watch-pids/-process-names)
///        └─ otherwise ..................→ MITM, advertise http/1.1 so h2 clients downgrade
/// </code>
/// </para>
///
/// <para>
/// Scope: keep-alive HTTP/1.1 (multiple requests per intercepted connection) for
/// plain HTTP + HTTPS-via-CONNECT, mocking short-circuit, selective decrypt + ALPN
/// blind-tunnel, and transparent WebSocket relay (handshake replayed, frames spliced
/// opaque — see <see cref="WebSocketRelay"/>). Streamed (<c>text/event-stream</c>)
/// responses are forwarded incrementally (chunked) with a capped tee to inspectors —
/// see <see cref="WriteStreamingResponseAsync"/>. Deferred hardening (tracked):
/// WebSocket frame inspection/mocking (plan §7).
/// </para>
/// </summary>
internal sealed class ProxyConnectionHandler(
    CertificateAuthority ca,
    UpstreamForwarder forwarder,
    PluginPipeline pipeline,
    HostWatchList watchList,
    ProcessFilter processFilter,
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
        catch (Exception ex) when (ConnectionTeardown.IsExpected(ex))
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
        // Parse + validate the authority BEFORE acknowledging the tunnel, so a malformed
        // target (bad port, unbracketed IPv6, junk) is refused with a 400 rather than
        // establishing a tunnel we can't actually use.
        if (!ConnectAuthorityParser.TryParse(connect.Target, defaultPort: 443, out var authority))
        {
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Malformed CONNECT target", ct).ConfigureAwait(false);
            return;
        }

        var host = authority.Host;
        var port = authority.Port;

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

        // Process filter (--watch-pids / --watch-process-names): like the Titanium engine,
        // a watched host whose owning process isn't watched is blind-tunnelled, never
        // decrypted. Resolving the PID shells out, so only do it when a filter is set.
        if (!processFilter.IsEmpty && !processFilter.IsWatchedProcess(GetClientPort(connection)))
        {
            logger.LogDebug("CONNECT {Host}:{Port} → blind-tunnel (process not watched)", host, port);
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

        await ServeConnectionAsync(tlsReader, tls, head, authority.UrlHost, port, ct).ConfigureAwait(false);
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

        var framing = Http1RequestReader.DetectBodyFraming(head.Headers);
        if (framing == RequestBodyFraming.Conflicting)
        {
            // Content-Length and chunked Transfer-Encoding disagree on where the body
            // ends — a request-smuggling vector (RFC 9112 §6.3.3). Refuse it.
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest,
                "Conflicting Content-Length and Transfer-Encoding", ct).ConfigureAwait(false);
            return false;
        }

        if (Http1RequestReader.HasExpectContinue(head.Headers))
        {
            // The client is waiting for a go-ahead before sending its body. We always
            // buffer and forward the body, so answer the expectation ourselves.
            await ResponseWriter.WriteContinueAsync(clientStream, ct).ConfigureAwait(false);
        }

        byte[] body;
        try
        {
            body = framing == RequestBodyFraming.Chunked
                ? await reader.ReadChunkedBodyAsync(ct).ConfigureAwait(false)
                : await reader.ReadBodyAsync(Http1RequestReader.GetContentLength(head.Headers), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Malformed chunked framing (bad chunk size, missing CRLF, truncated). The
            // connection's byte stream is no longer framable, so close after replying.
            logger.LogWarning(ex, "Malformed request body framing");
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Malformed request body", ct).ConfigureAwait(false);
            return false;
        }

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
            catch (Exception ex) when (ConnectionTeardown.IsExpected(ex))
            {
                // Client or origin closed the WebSocket mid-handshake/relay — normal teardown.
                logger.LogDebug(ex, "WebSocket relay to {Url} ended on connection close", absoluteUrl);
            }
            catch (Exception ex)
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

        OriginResponse origin;
        try
        {
            origin = await forwarder.ForwardAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pipeline.Forget(session.SessionId);
            logger.LogError(ex, "Error forwarding to origin {Url}", absoluteUrl);
            await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Upstream request failed", ct).ConfigureAwait(false);
            return false;
        }

        await using (origin.ConfigureAwait(false))
        {
            // text/event-stream: forward the body to the client piece-by-piece (chunked)
            // instead of buffering it, so events arrive live and an unbounded stream never
            // hangs the engine.
            if (origin.IsStreaming)
            {
                return await WriteStreamingResponseAsync(clientStream, session, origin, phase, keepAlive, ct)
                    .ConfigureAwait(false);
            }

            if (phase == RequestPhase.Watched)
            {
                session.SetOriginResponse(origin.Response);
                await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
                await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, keepAlive, ct).ConfigureAwait(false);
            }
            else
            {
                // NotWatched: pure passthrough, no response-phase plugins.
                await ResponseWriter.WriteAsync(clientStream, origin.Response, keepAlive, ct).ConfigureAwait(false);
            }

            return keepAlive;
        }
    }

    /// <summary>
    /// Forwards a streamed (<c>text/event-stream</c>) response to the client incrementally.
    /// For a watched session the response head + body are written between the
    /// <c>BeforeResponse</c> and <c>AfterResponse</c> plugin phases, and a capped copy of
    /// the body is exposed to read-only <c>AfterResponse</c> inspectors (e.g. OpenAI
    /// telemetry). <c>BeforeResponse</c> body replacement is not supported on streamed
    /// responses — the live origin body is always forwarded.
    /// </summary>
    private async Task<bool> WriteStreamingResponseAsync(
        Stream clientStream,
        CanonicalProxySession session,
        OriginResponse origin,
        RequestPhase phase,
        bool keepAlive,
        CancellationToken ct)
    {
        const int accumulateCap = (int)BodyModeResolver.DefaultInMemoryLimitBytes;

        if (phase == RequestPhase.Watched)
        {
            session.SetOriginResponse(origin.Response);
            await pipeline.RunStreamingResponseAsync(session, async innerCt =>
            {
                var accumulated = await StreamingResponseWriter.WriteAsync(
                    clientStream, session.MutableResponse!, origin.BodyStream!, keepAlive, accumulateCap, innerCt)
                    .ConfigureAwait(false);

                // Hand the captured stream to AfterResponse inspectors. Empty when the
                // stream exceeded the cap — those plugins then simply see no body.
                if (!accumulated.IsEmpty)
                {
                    session.MutableResponse!.SetBody(accumulated);
                }
            }, ct).ConfigureAwait(false);
        }
        else
        {
            // NotWatched: incremental passthrough, no plugins, no need to accumulate.
            await StreamingResponseWriter.WriteAsync(
                clientStream, origin.Response, origin.BodyStream!, keepAlive, accumulateCap: 0, ct)
                .ConfigureAwait(false);
        }

        return keepAlive;
    }

    /// <summary>
    /// Decides whether the connection may serve another request after this one.
    /// Persistent by default on HTTP/1.1 (RFC 9112 §9.3), opt-in on HTTP/1.0, and
    /// forced closed by <c>Connection: close</c>. Chunked bodies and
    /// <c>Expect: 100-continue</c> are now read/handled before this runs (the body is
    /// re-framed with <c>Content-Length</c> on forward), so they no longer force a close.
    /// </summary>
    internal static bool ShouldKeepAlive(ParsedRequestHead head)
    {
        string? connection = null;
        foreach (var (name, value) in head.Headers)
        {
            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                connection = value;
                break;
            }
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
        catch (Exception ex) when (ConnectionTeardown.IsExpected(ex))
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

    // The client's source port — the remote end of the connection the proxy accepted.
    // Used to resolve the owning process for the --watch-pids/--watch-process-names filter.
    private static int GetClientPort(ConnectionContext connection) =>
        connection.RemoteEndPoint is IPEndPoint endpoint ? endpoint.Port : 0;

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
