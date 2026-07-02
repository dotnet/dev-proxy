// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Relays a WebSocket connection between the client and the origin. A WebSocket
/// handshake cannot go through <see cref="UpstreamForwarder"/> (pooled
/// <see cref="HttpClient"/>): it needs the raw, long-lived socket that survives the
/// <c>101 Switching Protocols</c> response. This mirrors what the Titanium engine
/// does today — frames are relayed verbatim and opaque; no plugin inspects them.
///
/// <code>
///   client                         proxy                          origin
///     │  GET … Upgrade: websocket    │                              │
///     │ ───────────────────────────► │  (replay handshake, origin-  │
///     │                              │   form, preserving Upgrade/   │
///     │                              │   Connection/Sec-WebSocket-*) ─►
///     │                              │  ◄─ 101 Switching Protocols ──│
///     │ ◄── 101 (verbatim) ──────────│                              │
///     │ ◄═══════════ raw WebSocket frames spliced both ways ═══════► │
/// </code>
///
/// <para>
/// The <c>101</c> is written to the client <b>verbatim</b> (never via
/// <see cref="ResponseWriter"/>, which strips <c>Upgrade</c>/<c>Connection</c> and
/// injects <c>Content-Length</c> — that would corrupt the handshake).
/// </para>
///
/// <para>
/// <b>Deferred (tracked, see plan §7):</b> decoding frames into messages and exposing
/// them to plugins for inspection/mocking. Until then this is a transparent relay.
/// </para>
/// </summary>
internal sealed class WebSocketRelay(ILogger logger)
{
    private const int MaxResponseHeadBytes = 64 * 1024;

    /// <summary>
    /// Connects to the origin, replays the upgrade handshake, invokes
    /// <paramref name="onHandshakeResponse"/> with the origin's parsed response (so the
    /// caller can run the response pipeline / log it), writes that response back to the
    /// client verbatim, and — on <c>101</c> — relays WebSocket messages in both directions
    /// until either peer closes. Each relayed message is reported via
    /// <paramref name="onMessage"/> (when non-null) for HAR / reporting capture.
    /// </summary>
    public async Task RelayAsync(
        Stream clientStream,
        IHttpRequest request,
        Uri origin,
        Func<MutableHttpResponse, Task> onHandshakeResponse,
        Action<WebSocketMessageRecord>? onMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(onHandshakeResponse);

        var useTls = string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(origin.Host, origin.Port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "WebSocket connect to {Host}:{Port} failed", origin.Host, origin.Port);
            return;
        }

        // leaveInnerStreamOpen: false on the SslStream disposes the NetworkStream; the
        // NetworkStream is also owned by the `using` TcpClient. `await using` here
        // disposes whichever stream we end up relaying over.
        await using var originStream = await OpenOriginStreamAsync(tcp, origin, useTls, ct).ConfigureAwait(false);

        await WriteHandshakeAsync(originStream, request, origin, ct).ConfigureAwait(false);

        var head = await ReadResponseHeadAsync(originStream, ct).ConfigureAwait(false);
        if (head is null)
        {
            logger.LogDebug("WebSocket origin {Host} closed before sending a handshake response", origin.Host);
            return;
        }

        var (statusCode, reason, headers, rawHead, leftover) = head.Value;

        var response = new MutableHttpResponse(
            (HttpStatusCode)statusCode, HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty, reason);
        await onHandshakeResponse(response).ConfigureAwait(false);

        // Write the origin's handshake response to the client verbatim.
        await clientStream.WriteAsync(rawHead, ct).ConfigureAwait(false);
        await clientStream.FlushAsync(ct).ConfigureAwait(false);

        if (statusCode != (int)HttpStatusCode.SwitchingProtocols)
        {
            // Origin declined the upgrade. We've relayed its response; there's no
            // tunnel to splice. Close (a non-101 may carry a body we don't frame yet).
            logger.LogDebug("WebSocket origin {Host} declined upgrade with {Status}", origin.Host, statusCode);
            return;
        }

        // Extract sub-protocol from handshake response for WebSocket creation.
        var subProtocol = headers.GetFirst("Sec-WebSocket-Protocol")?.Value;

        logger.LogDebug("WebSocket {Scheme}://{Host}:{Port}{Path} established",
            useTls ? "wss" : "ws", origin.Host, origin.Port, origin.PathAndQuery);

        // Wrap both sides as WebSocket instances for frame-level relay and message
        // capture. Any leftover bytes (read past the response head) are prepended to
        // the origin stream so the first origin frame isn't lost.
#pragma warning disable CA2000 // PrefixedStream is disposed via await using below; ownership is clear.
        await using var prefixed = leftover.Length > 0 ? new PrefixedStream(leftover, originStream) : null;
#pragma warning restore CA2000
        Stream effectiveOriginStream = prefixed ?? originStream;

        using var clientWs = WebSocket.CreateFromStream(
            clientStream, new WebSocketCreationOptions { IsServer = true, SubProtocol = subProtocol, KeepAliveInterval = KeepAliveInterval });
        using var originWs = WebSocket.CreateFromStream(
            effectiveOriginStream, new WebSocketCreationOptions { IsServer = false, SubProtocol = subProtocol, KeepAliveInterval = KeepAliveInterval });

        await RelayWebSocketAsync(clientWs, originWs, onMessage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the origin stream, layering TLS (with http/1.1 ALPN, matching our downgrade
    /// policy) for <c>wss</c>. On a TLS failure the half-built <see cref="SslStream"/> is
    /// disposed before the exception propagates.
    /// </summary>
    private static async Task<Stream> OpenOriginStreamAsync(TcpClient tcp, Uri origin, bool useTls, CancellationToken ct)
    {
        var network = tcp.GetStream();
        if (!useTls)
        {
            return network;
        }

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = origin.Host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, ct).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ssl;
    }

    /// <summary>
    /// Replays the handshake to the origin in origin-form. WebSocket-essential headers
    /// (<c>Upgrade</c>, <c>Connection</c>, <c>Sec-WebSocket-*</c>) MUST be preserved —
    /// they are normally hop-by-hop but are exactly what the handshake needs — so only
    /// the proxy-scoped headers are dropped.
    /// </summary>
    private static async Task WriteHandshakeAsync(Stream origin, IHttpRequest request, Uri target, CancellationToken ct)
    {
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{request.Method} {target.PathAndQuery} HTTP/1.1\r\n");

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _ = builder.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }
        _ = builder.Append("\r\n");

        await origin.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
        await origin.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the origin's HTTP response head (status line + headers) up to the
    /// terminating CRLFCRLF, returning the parsed status/headers, the exact raw head
    /// bytes (to relay verbatim) and any bytes read past the head (the first frame).
    /// Returns null on EOF before a complete head arrived.
    /// </summary>
    private static async Task<(int StatusCode, string Reason, HeaderCollection Headers, byte[] RawHead, byte[] Leftover)?>
        ReadResponseHeadAsync(Stream origin, CancellationToken ct)
    {
        var accumulator = new List<byte>(512);
        var buffer = new byte[4096];

        while (true)
        {
            var terminator = Http1RequestReader.IndexOfDoubleCrlf(accumulator);
            if (terminator >= 0)
            {
                var headEnd = terminator + 4;
                var rawHead = accumulator.GetRange(0, headEnd).ToArray();
                var leftover = accumulator.Count > headEnd
                    ? accumulator.GetRange(headEnd, accumulator.Count - headEnd).ToArray()
                    : [];

                var headerText = Encoding.ASCII.GetString(accumulator.ToArray(), 0, terminator);
                var (statusCode, reason, headers) = ParseResponseHead(headerText);
                return (statusCode, reason, headers, rawHead, leftover);
            }

            if (accumulator.Count > MaxResponseHeadBytes)
            {
                throw new InvalidOperationException("WebSocket origin response head too large.");
            }

            var read = await origin.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            accumulator.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Parses a response head block: <c>HTTP/1.1 101 Switching Protocols</c> followed
    /// by header lines.
    /// </summary>
    internal static (int StatusCode, string Reason, HeaderCollection Headers) ParseResponseHead(string headerText)
    {
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Empty WebSocket origin response head.");
        }

        var statusLine = lines[0];
        var firstSpace = statusLine.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            throw new InvalidOperationException($"Malformed status line: '{statusLine}'.");
        }

        var afterVersion = statusLine[(firstSpace + 1)..].TrimStart();
        var secondSpace = afterVersion.IndexOf(' ', StringComparison.Ordinal);
        var codeToken = secondSpace < 0 ? afterVersion : afterVersion[..secondSpace];
        if (!int.TryParse(codeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidOperationException($"Malformed status code in '{statusLine}'.");
        }
        var reason = secondSpace < 0 ? string.Empty : afterVersion[(secondSpace + 1)..];

        var headers = new HeaderCollection();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            headers.Add(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return (statusCode, reason, headers);
    }

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private const int ReceiveChunkSize = 8 * 1024;

    /// <summary>
    /// Relays complete WebSocket messages between <paramref name="client"/> and
    /// <paramref name="origin"/> in both directions, capturing each message via
    /// <paramref name="onMessage"/>. Runs until either peer closes or the token fires.
    /// </summary>
    private static async Task RelayWebSocketAsync(
        WebSocket client,
        WebSocket origin,
        Action<WebSocketMessageRecord>? onMessage,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var clientToOrigin = RelayOneDirectionAsync(
            client, origin, WebSocketMessageDirection.Send, onMessage, linked.Token);
        var originToClient = RelayOneDirectionAsync(
            origin, client, WebSocketMessageDirection.Receive, onMessage, linked.Token);

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
            catch (Exception ex) when (ConnectionTeardown.IsExpected(ex))
            {
                // Either peer closed — normal teardown.
            }
        }
    }

    /// <summary>
    /// Receives complete messages from <paramref name="source"/> (reassembling
    /// fragments) and forwards them to <paramref name="destination"/>, recording
    /// each message via <paramref name="onMessage"/>.
    /// </summary>
    private static async Task RelayOneDirectionAsync(
        WebSocket source,
        WebSocket destination,
        WebSocketMessageDirection direction,
        Action<WebSocketMessageRecord>? onMessage,
        CancellationToken ct)
    {
        var buffer = new byte[ReceiveChunkSize];

        while (source.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            // Reassemble fragments into a complete message.
            var assembled = new List<byte>(ReceiveChunkSize);
            WebSocketReceiveResult result;
            do
            {
                result = await source.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Propagate close to the other side.
                    if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await destination.CloseOutputAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            ct).ConfigureAwait(false);
                    }

                    onMessage?.Invoke(new WebSocketMessageRecord(
                        direction, WebSocketMessageType.Close, ReadOnlyMemory<byte>.Empty, DateTimeOffset.UtcNow));
                    return;
                }

                assembled.AddRange(buffer.AsSpan(0, result.Count));
            }
            while (!result.EndOfMessage);

            var data = assembled.ToArray();

            // Forward the complete message to the other side.
            await destination.SendAsync(
                data, result.MessageType, endOfMessage: true, ct).ConfigureAwait(false);

            onMessage?.Invoke(new WebSocketMessageRecord(
                direction, result.MessageType, data, DateTimeOffset.UtcNow));
        }
    }
}

/// <summary>
/// A read-only stream wrapper that prepends a byte prefix to an inner stream.
/// Used to replay leftover bytes read past the HTTP response head before the
/// inner stream is wrapped as a WebSocket.
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixOffset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixOffset < prefix.Length)
        {
            var available = Math.Min(count, prefix.Length - _prefixOffset);
            Array.Copy(prefix, _prefixOffset, buffer, offset, available);
            _prefixOffset += available;
            return available;
        }
        return inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixOffset < prefix.Length)
        {
            var available = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
            prefix.AsMemory(_prefixOffset, available).CopyTo(buffer);
            _prefixOffset += available;
            return available;
        }
        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Don't dispose inner — it's owned by the caller (TcpClient/SslStream).
        base.Dispose(disposing);
    }
}
