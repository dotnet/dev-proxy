// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.WebSockets;
using System.Text;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// Direction of a captured WebSocket message relative to the client.
/// </summary>
public enum WebSocketMessageDirection
{
    /// <summary>Client → origin (the client sent this message).</summary>
    Send,

    /// <summary>Origin → client (the server sent this message).</summary>
    Receive
}

/// <summary>
/// A timestamped record of a WebSocket message that flowed through the proxy,
/// captured for reporting (e.g.\ HAR generation). Follows the Chrome/mitmproxy
/// <c>_webSocketMessages</c> convention.
/// </summary>
/// <param name="Direction">Whether the message was sent by the client or received from the server.</param>
/// <param name="Type">
/// The WebSocket message type (<see cref="WebSocketMessageType.Text"/>,
/// <see cref="WebSocketMessageType.Binary"/>, or <see cref="WebSocketMessageType.Close"/>).
/// </param>
/// <param name="Data">The reassembled message payload (empty for close frames).</param>
/// <param name="Timestamp">When the message was observed by the proxy.</param>
public sealed record WebSocketMessageRecord(
    WebSocketMessageDirection Direction,
    WebSocketMessageType Type,
    ReadOnlyMemory<byte> Data,
    DateTimeOffset Timestamp)
{
    /// <summary>The payload decoded as UTF-8 text (useful for text messages).</summary>
    public string Text => Encoding.UTF8.GetString(Data.Span);
}
