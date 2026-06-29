// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Completes a WebSocket upgrade on the client's behalf and runs a plugin-supplied
/// handler over the live connection — the WebSocket analogue of a mocked HTTP response.
/// Unlike <see cref="WebSocketRelay"/>, the origin is NEVER contacted: the proxy itself
/// is the WebSocket server.
///
/// <code>
///   client                              proxy (mock server)            origin
///     │  GET … Upgrade: websocket         │                              │
///     │ ────────────────────────────────▶ │  compute Sec-WebSocket-Accept  (never
///     │ ◀── 101 Switching Protocols ───────│  WebSocket.CreateFromStream    dialed)
///     │ ◀═══ scripted frames ════════════▶ │  run plugin handler
/// </code>
///
/// <para>
/// Framing is delegated to the framework <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/>
/// (server mode) — no hand-rolled frame codec. The handshake <c>101</c> is written to the
/// client verbatim BEFORE wrapping the stream, exactly as a real WebSocket server would.
/// </para>
/// </summary>
internal sealed class WebSocketMockResponder(ILogger logger)
{
    // RFC 6455 §1.3 — the magic GUID appended to Sec-WebSocket-Key before hashing.
    private const string HandshakeMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Computes the handshake accept token, writes the <c>101</c> to the client verbatim,
    /// invokes <paramref name="onHandshakeResponse"/> (so the caller can run the response
    /// pipeline / flush the request log), wraps the stream as a server-side
    /// <see cref="WebSocket"/>, and runs <paramref name="handler"/> until it returns.
    /// </summary>
    public async Task RespondAsync(
        Stream clientStream,
        IHttpRequest request,
        Func<IWebSocketConnection, CancellationToken, Task> handler,
        Func<MutableHttpResponse, Task> onHandshakeResponse,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(onHandshakeResponse);

        var key = request.Headers.GetFirst("Sec-WebSocket-Key")?.Value;
        if (string.IsNullOrEmpty(key))
        {
            // Not a valid handshake (no client nonce). Refuse rather than upgrade.
            logger.LogDebug("WebSocket mock: request to {Url} is missing Sec-WebSocket-Key", request.Url);
            await WriteRawAsync(clientStream, BuildBadRequest(), ct).ConfigureAwait(false);
            return;
        }

        // RFC 6455 §4.2.2: if the client offered sub-protocols, echo the first one.
        var subProtocol = request.Headers.GetFirst("Sec-WebSocket-Protocol")?.Value?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var accept = ComputeAcceptKey(key);
        var (rawHead, headers) = BuildHandshakeResponse(accept, subProtocol);

        var response = new MutableHttpResponse(
            HttpStatusCode.SwitchingProtocols, HttpVersion.Version11, headers,
            ReadOnlyMemory<byte>.Empty, "Switching Protocols");
        await onHandshakeResponse(response).ConfigureAwait(false);

        await WriteRawAsync(clientStream, rawHead, ct).ConfigureAwait(false);

        // The stream now carries WebSocket frames; let the framework own the codec.
        // Ownership of the WebSocket transfers to FramedWebSocketConnection, which is
        // disposed by the `await using` below.
#pragma warning disable CA2000
        var webSocket = WebSocket.CreateFromStream(
            clientStream, isServer: true, subProtocol: subProtocol, keepAliveInterval: KeepAliveInterval);
#pragma warning restore CA2000
        await using var connection = new FramedWebSocketConnection(webSocket);

        logger.LogDebug("WebSocket mock established for {Url}", request.Url);
        await handler(connection, ct).ConfigureAwait(false);
        await connection.CloseAsync(ct).ConfigureAwait(false);
    }

    private static string ComputeAcceptKey(string clientKey)
    {
        // SHA1 is mandated by the WebSocket handshake (RFC 6455 §4.2.2) — this is a
        // protocol constant, not a security-sensitive hash.
#pragma warning disable CA5350
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + HandshakeMagicGuid));
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }

    private static (byte[] RawHead, HeaderCollection Headers) BuildHandshakeResponse(string accept, string? subProtocol)
    {
        var headers = new HeaderCollection();
        headers.Add("Upgrade", "websocket");
        headers.Add("Connection", "Upgrade");
        headers.Add("Sec-WebSocket-Accept", accept);
        if (!string.IsNullOrEmpty(subProtocol))
        {
            headers.Add("Sec-WebSocket-Protocol", subProtocol);
        }

        var builder = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\n");
        foreach (var header in headers)
        {
            _ = builder.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }
        _ = builder.Append("\r\n");

        return (Encoding.ASCII.GetBytes(builder.ToString()), headers);
    }

    private static byte[] BuildBadRequest() => Encoding.ASCII.GetBytes(
        "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

    private static async Task WriteRawAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Adapts a framework server-side <see cref="WebSocket"/> to
/// <see cref="IWebSocketConnection"/>: sends are one-shot complete messages; receives
/// reassemble fragments into a single message. Close is sent via
/// <see cref="WebSocket.CloseOutputAsync"/> (fire-and-forget) so a mock never blocks
/// waiting for the client's close echo.
/// </summary>
internal sealed class FramedWebSocketConnection(WebSocket webSocket) : IWebSocketConnection, IAsyncDisposable
{
    private const int ReceiveChunkSize = 8 * 1024;

    public Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return webSocket.SendAsync(
            Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        webSocket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).AsTask();

    public async Task<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
        {
            return null;
        }

        var buffer = new byte[ReceiveChunkSize];
        var assembled = new List<byte>(ReceiveChunkSize);
        WebSocketReceiveResult result;
        do
        {
            try
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Connection ended abruptly without a clean close frame.
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WebSocketMessage(WebSocketMessageType.Close, ReadOnlyMemory<byte>.Empty);
            }

            assembled.AddRange(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);

        return new WebSocketMessage(result.MessageType, assembled.ToArray());
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer already gone; nothing to close.
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        webSocket.Dispose();
        return ValueTask.CompletedTask;
    }
}
