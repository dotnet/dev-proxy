// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy.Http;

namespace DevProxy.Proxy.Kestrel.Http;

/// <summary>
/// Concrete <see cref="IProxySession"/> for the Kestrel engine. One instance per
/// logical request/response exchange; <see cref="SessionId"/> is stable across the
/// request and response phases of the same exchange even when the underlying TCP
/// connection is reused (HTTP keep-alive).
/// </summary>
public sealed class CanonicalProxySession : IProxySession
{
    private static readonly Version DefaultHttpVersion = System.Net.HttpVersion.Version11;

    private MutableHttpResponse? _response;

    public CanonicalProxySession(string sessionId, MutableHttpRequest request, int? processId)
        : this(sessionId, request, processId, requestId: 0)
    {
    }

    public CanonicalProxySession(string sessionId, MutableHttpRequest request, int? processId, int requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        SessionId = sessionId;
        Request = request;
        ProcessId = processId;
        RequestId = requestId;
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <summary>
    /// A stable per-exchange integer used to group request- and response-phase log
    /// entries in the console formatter (mirrors the Titanium engine's hashcode key).
    /// </summary>
    public int RequestId { get; }

    /// <inheritdoc />
    public IHttpRequest Request { get; }

    /// <inheritdoc />
    public IHttpResponse? Response => _response;

    /// <inheritdoc />
    public int? ProcessId { get; }

    /// <inheritdoc />
    public bool HasResponse => _response is not null;

    /// <summary>
    /// True when the current <see cref="Response"/> was produced by a plugin (via
    /// <see cref="Respond(string, HttpStatusCode, IEnumerable{IHttpHeader})"/>)
    /// rather than received from the origin. The engine uses this to short-circuit
    /// upstream forwarding.
    /// </summary>
    public bool RespondedByPlugin { get; private set; }

    /// <summary>The concrete response, for engine write-back. Null until set.</summary>
    public MutableHttpResponse? MutableResponse => _response;

    /// <summary>
    /// Sets the response received from the origin. Does not flag the exchange as
    /// plugin-mocked.
    /// </summary>
    public void SetOriginResponse(MutableHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _response = response;
    }

    /// <inheritdoc />
    public void Respond(string body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);

        var response = new MutableHttpResponse(statusCode, DefaultHttpVersion, new HeaderCollection(headers), ReadOnlyMemory<byte>.Empty);
        response.SetBodyString(body);
        _response = response;
        RespondedByPlugin = true;
    }

    /// <inheritdoc />
    public void Respond(ReadOnlyMemory<byte> body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var response = new MutableHttpResponse(statusCode, DefaultHttpVersion, new HeaderCollection(headers), ReadOnlyMemory<byte>.Empty);
        response.SetBody(body);
        _response = response;
        RespondedByPlugin = true;
    }
}
