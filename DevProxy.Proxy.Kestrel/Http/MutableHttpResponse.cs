// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy.Http;

namespace DevProxy.Proxy.Kestrel.Http;

/// <summary>
/// In-memory <see cref="IHttpResponse"/> — either received from the origin and
/// decompressed, or synthesized by a plugin via
/// <see cref="IProxySession.Respond(string, HttpStatusCode, IEnumerable{IHttpHeader})"/>.
/// </summary>
public sealed class MutableHttpResponse : MutableHttpMessage, IHttpResponse
{
    public MutableHttpResponse(
        HttpStatusCode statusCode,
        Version httpVersion,
        HeaderCollection headers,
        ReadOnlyMemory<byte> body,
        string? statusDescription = null)
        : base(headers, body)
    {
        ArgumentNullException.ThrowIfNull(httpVersion);
        StatusCode = statusCode;
        HttpVersion = httpVersion;
        StatusDescription = statusDescription;
    }

    /// <inheritdoc />
    public HttpStatusCode StatusCode { get; set; }

    /// <inheritdoc />
    public string? StatusDescription { get; set; }

    /// <inheritdoc />
    public Version HttpVersion { get; }
}
