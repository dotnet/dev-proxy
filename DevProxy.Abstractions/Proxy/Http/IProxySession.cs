// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// A single logical request/response exchange — the canonical replacement for
/// the engine-specific session type that plugins previously consumed via
/// <c>Session.HttpClient.Request</c> / <c>.Response</c>.
///
/// <para>
/// <b>Lifetime &amp; identity.</b> One TCP connection can carry many exchanges
/// (HTTP keep-alive), a CONNECT tunnel, or a WebSocket session. Each exchange
/// gets a stable logical <see cref="SessionId"/>. Plugins MUST key per-exchange
/// state on <see cref="SessionId"/> and never on object identity / hash codes —
/// reusing a connection must not leak state between exchanges.
/// </para>
/// </summary>
public interface IProxySession
{
    /// <summary>
    /// Stable, logical identifier for this exchange. Unique per request/response
    /// pair even when the underlying connection is reused.
    /// </summary>
    string SessionId { get; }

    /// <summary>The intercepted request.</summary>
    IHttpRequest Request { get; }

    /// <summary>
    /// The response, once available (origin response received, or a plugin
    /// produced one via <see cref="Respond(string, HttpStatusCode, IEnumerable{IHttpHeader})"/>).
    /// <c>null</c> during the request phase before any response exists.
    /// </summary>
    IHttpResponse? Response { get; }

    /// <summary>
    /// PID of the local process that originated the request, when the engine
    /// could resolve it; otherwise <c>null</c>.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// True once a response has been produced for this exchange (by the origin
    /// or a plugin). Equivalent to <see cref="Response"/> being non-null.
    /// </summary>
    bool HasResponse { get; }

    /// <summary>
    /// Produces a mocked text response and short-circuits the exchange: no
    /// request is sent upstream and <see cref="Response"/> becomes non-null. This
    /// is the canonical mocking primitive (the replacement for
    /// <c>GenericResponse</c>).
    /// </summary>
    void Respond(string body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers);

    /// <summary>
    /// Produces a mocked binary response and short-circuits the exchange.
    /// </summary>
    void Respond(ReadOnlyMemory<byte> body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers);

    /// <summary>
    /// Mocks a WebSocket exchange: when this request is a WebSocket upgrade
    /// (<see cref="IHttpRequest.IsWebSocketRequest"/>), the engine completes the
    /// handshake itself (no origin is contacted) and then runs <paramref name="handler"/>
    /// over the live connection so the plugin can script the conversation. This is the
    /// WebSocket analogue of <see cref="Respond(string, HttpStatusCode, IEnumerable{IHttpHeader})"/>:
    /// the plugin declares intent here during <c>BeforeRequest</c> and the engine executes it.
    /// </summary>
    void HandleWebSocket(Func<IWebSocketConnection, CancellationToken, Task> handler);
}
