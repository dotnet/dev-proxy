// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using TitaniumMessage = Titanium.Web.Proxy.Http.RequestResponseBase;

namespace DevProxy.Proxy.Titanium;

/// <summary>
/// Shared projection of a Titanium <see cref="TitaniumMessage"/> (the common base
/// of request and response) onto the canonical <see cref="IHttpMessage"/> surface.
///
/// <para>
/// Body access mirrors the engine's contract: Titanium throws
/// <c>BodyNotFoundException</c> when a body is accessed before one exists, so
/// <see cref="Body"/>/<see cref="BodyString"/> are guarded by <c>HasBody</c> and
/// return empty otherwise. The Dev Proxy engine force-reads watched
/// request/response bodies before invoking plugins, so these synchronous
/// accessors are populated by the time a plugin runs.
/// </para>
///
/// <para>
/// Titanium exposes no public body setter on the message itself, so mutations are
/// routed through <paramref name="setBody"/> (the session's
/// <c>SetRequestBody</c>/<c>SetResponseBody</c>, which also keep Titanium's
/// internal state consistent). An adapter constructed without a setter is
/// read-only and <see cref="SetBody"/> throws.
/// </para>
/// </summary>
public abstract class TitaniumHttpMessageAdapter : IHttpMessage
{
    private readonly TitaniumMessage _message;
    private readonly Action<byte[]>? _setBody;
    private readonly TitaniumHeaderCollection _headers;

    /// <param name="message">The Titanium request or response to wrap.</param>
    /// <param name="setBody">
    /// Optional body setter that keeps the owning session consistent. When
    /// <c>null</c>, the adapter is read-only and <see cref="SetBody"/> throws.
    /// </param>
    protected TitaniumHttpMessageAdapter(TitaniumMessage message, Action<byte[]>? setBody)
    {
        ArgumentNullException.ThrowIfNull(message);
        _message = message;
        _setBody = setBody;
        _headers = new TitaniumHeaderCollection(message.Headers);
    }

    /// <inheritdoc />
    public IHeaderCollection Headers => _headers;

    /// <inheritdoc />
    public string? ContentType => _message.ContentType;

    /// <inheritdoc />
    public bool HasBody => _message.HasBody;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Body =>
        _message.HasBody && _message.Body is { } bytes ? bytes : ReadOnlyMemory<byte>.Empty;

    /// <inheritdoc />
    public string BodyString => _message.HasBody ? _message.BodyString : string.Empty;

    /// <inheritdoc />
    public void SetBody(ReadOnlyMemory<byte> body, string? contentType = null)
    {
        if (_setBody is null)
        {
            throw new InvalidOperationException(
                "This message cannot be mutated because it was not constructed with a body setter. " +
                "Body mutation requires a session-bound adapter (the engine supplies the session's " +
                "SetRequestBody/SetResponseBody). Titanium exposes no public body setter on the message itself.");
        }

        _setBody(body.ToArray());

        if (contentType is not null)
        {
            _message.ContentType = contentType;
        }
    }

    /// <inheritdoc />
    public void SetBodyString(string body, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        SetBody(Encoding.UTF8.GetBytes(body), contentType);
    }
}
