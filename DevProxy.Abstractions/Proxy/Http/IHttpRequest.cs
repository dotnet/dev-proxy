// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// An intercepted HTTP request in the canonical Dev Proxy model.
/// </summary>
public interface IHttpRequest : IHttpMessage
{
    /// <summary>Absolute request URI. The canonical identity of the request target.</summary>
    Uri RequestUri { get; }

    /// <summary>
    /// Convenience accessor equal to <see cref="RequestUri"/>.<c>AbsoluteUri</c>.
    /// </summary>
    string Url { get; set; }

    /// <summary>HTTP method (e.g. <c>GET</c>, <c>POST</c>), upper-cased.</summary>
    string Method { get; }

    /// <summary>Negotiated HTTP version for this request.</summary>
    Version HttpVersion { get; }

    /// <summary>
    /// True when this request is a WebSocket upgrade handshake
    /// (<c>Upgrade: websocket</c>).
    /// </summary>
    bool IsWebSocketRequest { get; }
}
