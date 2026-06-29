// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Abstractions.Proxy.Http;

namespace DevProxy.Proxy.Kestrel.Http;

/// <summary>
/// In-memory <see cref="IHttpMessage"/> backing the Kestrel engine's canonical
/// model. Bodies are always stored decompressed (the engine decodes
/// <c>Content-Encoding</c> on read); writing a body keeps <c>Content-Length</c>
/// in sync per <see cref="ForwardingInvariants"/>.
/// </summary>
public abstract class MutableHttpMessage : IHttpMessage
{
    private ReadOnlyMemory<byte> _body;

    private protected MutableHttpMessage(HeaderCollection headers, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Headers = headers;
        _body = body;
    }

    /// <inheritdoc />
    public IHeaderCollection Headers { get; }

    /// <inheritdoc />
    public string? ContentType => Headers.GetFirst("Content-Type")?.Value;

    /// <inheritdoc />
    public bool HasBody => !_body.IsEmpty;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Body => _body;

    /// <inheritdoc />
    public string BodyString => _body.IsEmpty ? string.Empty : ResolveEncoding().GetString(_body.Span);

    /// <inheritdoc />
    public void SetBody(ReadOnlyMemory<byte> body, string? contentType = null)
    {
        _body = body;
        if (contentType is not null)
        {
            Headers.Replace("Content-Type", contentType);
        }
        Headers.Replace("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public void SetBodyString(string body, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        SetBody(Encoding.UTF8.GetBytes(body), contentType);
    }

    private Encoding ResolveEncoding()
    {
        var contentType = ContentType;
        if (contentType is not null)
        {
            var marker = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var charset = contentType[(marker + "charset=".Length)..].Trim().Trim('"');
                var separator = charset.IndexOf(';', StringComparison.Ordinal);
                if (separator >= 0)
                {
                    charset = charset[..separator].Trim();
                }

                try
                {
                    return Encoding.GetEncoding(charset);
                }
                catch (ArgumentException)
                {
                    // Unknown charset: fall back to UTF-8.
                }
            }
        }

        return Encoding.UTF8;
    }
}
