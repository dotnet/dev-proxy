// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;

namespace DevProxy.Proxy.Kestrel.Http;

/// <summary>
/// In-memory <see cref="IHttpRequest"/> parsed from the wire by the Kestrel engine.
/// </summary>
public sealed class MutableHttpRequest : MutableHttpMessage, IHttpRequest
{
    private Uri _requestUri;

    public MutableHttpRequest(
        string method,
        Uri requestUri,
        Version httpVersion,
        HeaderCollection headers,
        ReadOnlyMemory<byte> body)
        : base(headers, body)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(httpVersion);

        Method = method.ToUpperInvariant();
        _requestUri = requestUri;
        HttpVersion = httpVersion;
    }

    /// <inheritdoc />
    public Uri RequestUri => _requestUri;

    /// <inheritdoc />
    public string Url
    {
        get => _requestUri.AbsoluteUri;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            _requestUri = new Uri(value, UriKind.Absolute);
        }
    }

    /// <inheritdoc />
    public string Method { get; }

    /// <inheritdoc />
    public Version HttpVersion { get; }

    /// <inheritdoc />
    public bool IsWebSocketRequest
    {
        get
        {
            var upgrade = Headers.GetFirst("Upgrade")?.Value;
            return upgrade is not null
                && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase);
        }
    }
}
