// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.WebSockets;
using System.Text;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// A single WebSocket message handed to / produced by a plugin that mocks a
/// WebSocket exchange. The <see cref="Type"/> reuses the framework
/// <see cref="WebSocketMessageType"/> (<c>Text</c>/<c>Binary</c>/<c>Close</c>) so the
/// abstraction doesn't invent a parallel enum.
/// </summary>
/// <param name="Type">Whether the payload is text, binary, or a close signal.</param>
/// <param name="Data">
/// The (already reassembled) message payload. Empty for <see cref="WebSocketMessageType.Close"/>.
/// </param>
public sealed record WebSocketMessage(WebSocketMessageType Type, ReadOnlyMemory<byte> Data)
{
    /// <summary>The payload decoded as UTF-8 text (useful for <c>Text</c> messages).</summary>
    public string Text => Encoding.UTF8.GetString(Data.Span);
}

/// <summary>
/// A duplex WebSocket channel the engine hands to a plugin after it has completed the
/// upgrade handshake on the client's behalf. The engine owns the transport (framing via
/// the framework <see cref="WebSocket"/>); the plugin owns the behavior (what to send,
/// how to react to received messages). This mirrors the request/response mocking split:
/// the plugin declares intent, the engine executes it.
///
/// <code>
///   plugin handler                         engine (IWebSocketConnection)
///   ──────────────                         ─────────────────────────────
///   SendTextAsync("welcome")  ───────────▶ WebSocket.SendAsync(Text)
///   var m = ReceiveAsync()    ◀─────────── WebSocket.ReceiveAsync (reassembled)
///   if m.Text == "ping" → SendTextAsync("pong")
///   CloseAsync()              ───────────▶ WebSocket.CloseOutputAsync
/// </code>
/// </summary>
public interface IWebSocketConnection
{
    /// <summary>Sends a UTF-8 text message to the client.</summary>
    Task SendTextAsync(string message, CancellationToken cancellationToken);

    /// <summary>Sends a binary message to the client.</summary>
    Task SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>
    /// Receives the next complete message from the client (fragments are reassembled).
    /// Returns a <see cref="WebSocketMessageType.Close"/> message when the client closes,
    /// and <c>null</c> when the underlying connection ends without a close frame.
    /// </summary>
    Task<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends a normal-closure close frame to the client (idempotent).</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
