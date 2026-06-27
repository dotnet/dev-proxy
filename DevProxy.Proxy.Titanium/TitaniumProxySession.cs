// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy.Http;
using Titanium.Web.Proxy.EventArguments;
using TitaniumHttpHeader = Titanium.Web.Proxy.Models.HttpHeader;

namespace DevProxy.Proxy.Titanium;

/// <summary>
/// Projects a Titanium <see cref="SessionEventArgs"/> onto the canonical
/// <see cref="IProxySession"/>.
///
/// <para>
/// Titanium always exposes a non-null <c>HttpClient.Response</c> object, even
/// before an upstream response has been received. A response is considered
/// present once its status code is non-zero (the upstream replied, or a plugin
/// produced a mock via <see cref="Respond(string, HttpStatusCode, IEnumerable{IHttpHeader})"/>).
/// </para>
/// </summary>
public sealed class TitaniumProxySession : IProxySession
{
    private readonly SessionEventArgs _session;
    private readonly TitaniumRequestAdapter _request;
    private TitaniumResponseAdapter? _response;

    /// <param name="sessionId">The logical session identifier for this exchange.</param>
    /// <param name="session">The Titanium session to wrap.</param>
    public TitaniumProxySession(string sessionId, SessionEventArgs session)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(session);

        SessionId = sessionId;
        _session = session;
        _request = new TitaniumRequestAdapter(session.HttpClient.Request, session.SetRequestBody);
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public IHttpRequest Request => _request;

    /// <inheritdoc />
    public IHttpResponse? Response
    {
        get
        {
            if (!HasResponse)
            {
                return null;
            }

            _response ??= new TitaniumResponseAdapter(_session.HttpClient.Response, _session.SetResponseBody);
            return _response;
        }
    }

    /// <inheritdoc />
    public int? ProcessId => _session.HttpClient.ProcessId is { } processId ? processId.Value : null;

    /// <inheritdoc />
    public bool HasResponse => _session.HttpClient.Response.StatusCode != 0;

    /// <inheritdoc />
    public void Respond(string body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);
        _session.GenericResponse(body, statusCode, ToTitaniumHeaders(headers));
    }

    /// <inheritdoc />
    public void Respond(ReadOnlyMemory<byte> body, HttpStatusCode statusCode, IEnumerable<IHttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _session.GenericResponse(body.ToArray(), statusCode, ToTitaniumHeaders(headers));
    }

    private static IEnumerable<TitaniumHttpHeader> ToTitaniumHeaders(IEnumerable<IHttpHeader> headers) =>
        headers.Select(h => new TitaniumHttpHeader(h.Name, h.Value));
}
