// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Reads a sequence of HTTP/1.x request messages off one connection stream, owning
/// the buffer of bytes that have been read but not yet consumed. This is what makes
/// keep-alive correct: bytes read past one message's body (a pipelined next request)
/// are retained and handed to the following <see cref="ReadHeadAsync"/> instead of
/// being dropped.
///
/// <code>
///   stream ──read 4096──►  _pending  ──► [find CRLFCRLF] ──► head
///                                          │ leftover
///                                          ▼
///                          ReadBodyAsync(Content-Length) consumes leftover first,
///                          then the stream; any surplus stays in _pending for the
///                          next request (HTTP pipelining).
/// </code>
///
/// <para>
/// One instance per connection (or per decrypted TLS session). Not thread-safe:
/// the connection handler drives it sequentially (read head → read body → repeat).
/// </para>
///
/// <para>
/// <b>Deferred (tracked hardening):</b> chunked transfer-decoding + trailers and
/// <c>Expect: 100-continue</c>. Until then the connection handler refuses keep-alive
/// for any request that uses those (see <c>ProxyConnectionHandler.ShouldKeepAlive</c>),
/// so an unframable body never corrupts a subsequent request.
/// </para>
/// </summary>
internal sealed class Http1ConnectionReader(Stream stream)
{
    private const int ReadChunkBytes = 4096;
    private byte[] _pending = [];

    /// <summary>
    /// Reads the next request head, or <c>null</c> on a clean EOF (the peer closed the
    /// connection between requests, or mid-headers before a complete block arrived).
    /// </summary>
    public async Task<ParsedRequestHead?> ReadHeadAsync(CancellationToken ct)
    {
        var accumulator = new List<byte>(_pending);
        _pending = [];
        var buffer = new byte[ReadChunkBytes];

        while (true)
        {
            var terminator = Http1RequestReader.IndexOfDoubleCrlf(accumulator);
            if (terminator >= 0)
            {
                var headerText = Encoding.ASCII.GetString(accumulator.ToArray(), 0, terminator);
                _pending = Slice(accumulator, terminator + 4);
                return Http1RequestReader.ParseHead(headerText);
            }

            if (accumulator.Count > Http1RequestReader.MaxHeaderBlockBytes)
            {
                throw new InvalidOperationException("Request header block too large.");
            }

            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                // EOF: a clean close between requests, or a truncated header block.
                return null;
            }
            accumulator.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="contentLength"/> body bytes, consuming buffered
    /// bytes first and then the stream. Any bytes buffered beyond the body (a pipelined
    /// next request) are retained for the following <see cref="ReadHeadAsync"/>.
    /// </summary>
    public async Task<byte[]> ReadBodyAsync(int contentLength, CancellationToken ct)
    {
        if (contentLength <= 0)
        {
            return [];
        }

        var body = new byte[contentLength];
        var copied = Math.Min(_pending.Length, contentLength);
        Array.Copy(_pending, body, copied);
        _pending = _pending.Length > copied ? _pending[copied..] : [];

        var offset = copied;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }

        return body;
    }

    private static byte[] Slice(List<byte> accumulator, int start) =>
        start >= accumulator.Count ? [] : accumulator.GetRange(start, accumulator.Count - start).ToArray();
}
