// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// Common surface shared by <see cref="IHttpRequest"/> and
/// <see cref="IHttpResponse"/>.
///
/// <para>
/// <b>Body visibility contract:</b> <see cref="Body"/> and
/// <see cref="BodyString"/> always expose the <i>decompressed</i> payload
/// (Content-Encoding removed) regardless of how it travelled on the wire. The
/// engine is responsible for transparently decoding on read and re-encoding /
/// fixing <c>Content-Length</c> and <c>Content-Encoding</c> on write-back. See
/// <see cref="ForwardingInvariants"/>.
/// </para>
///
/// <para>
/// The body is only materialized when the engine buffers it for the exchange. For
/// ordinary (non-streamed) responses the full body is buffered and is readable and
/// mutable. For streamed responses (<c>text/event-stream</c>) the body is forwarded
/// live and only a capped copy is retained for read-only inspection; a streaming
/// pass-through exchange retains nothing, so accessing <see cref="Body"/> then yields
/// an empty buffer and <see cref="HasBody"/> may be true while the bytes are not
/// available to inspect.
/// </para>
/// </summary>
public interface IHttpMessage
{
    /// <summary>The message headers.</summary>
    IHeaderCollection Headers { get; }

    /// <summary>
    /// Value of the <c>Content-Type</c> header, or <c>null</c> when absent.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// True when the message carries (or is declared to carry) a body. This can
    /// be true even when the body is not buffered for inspection.
    /// </summary>
    bool HasBody { get; }

    /// <summary>
    /// The decompressed body bytes when buffered for this exchange; otherwise an
    /// empty buffer. See the body-visibility contract on <see cref="IHttpMessage"/>.
    /// </summary>
    ReadOnlyMemory<byte> Body { get; }

    /// <summary>
    /// The decompressed body decoded as text using the charset from
    /// <see cref="ContentType"/> (UTF-8 when unspecified). Empty when the body is
    /// not buffered.
    /// </summary>
    string BodyString { get; }

    /// <summary>
    /// Replaces the body with the given decompressed bytes and updates
    /// <c>Content-Length</c>. When <paramref name="contentType"/> is supplied the
    /// <c>Content-Type</c> header is set accordingly.
    /// </summary>
    void SetBody(ReadOnlyMemory<byte> body, string? contentType = null);

    /// <summary>
    /// Replaces the body with the UTF-8 encoding of <paramref name="body"/> and
    /// updates <c>Content-Length</c>. When <paramref name="contentType"/> is
    /// supplied the <c>Content-Type</c> header is set accordingly.
    /// </summary>
    void SetBodyString(string body, string? contentType = null);
}
