// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
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
///                          ReadBodyAsync(Content-Length)  ─┐ consume leftover first,
///                          ReadChunkedBodyAsync()          ─┘ then the stream; any
///                          surplus stays in _pending for the next request (pipelining).
/// </code>
///
/// <para>
/// Chunked decoding (<see cref="ReadChunkedBodyAsync"/>) buffers the decoded body and
/// drops trailers — the body is re-framed with <c>Content-Length</c> on forward, so
/// trailers have no place to go. Reading them off the wire still keeps the connection
/// correctly framed for the next pipelined request.
/// </para>
///
/// <para>
/// One instance per connection (or per decrypted TLS session). Not thread-safe:
/// the connection handler drives it sequentially (read head → read body → repeat).
/// </para>
/// </summary>
internal sealed class Http1ConnectionReader(Stream stream)
{
    private const int ReadChunkBytes = 4096;
    private const int MaxChunkLineBytes = 64 * 1024;
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
    /// next request) are retained for the following <see cref="ReadHeadAsync"/>. On a
    /// premature EOF the partial body read so far is returned.
    /// </summary>
    public async Task<byte[]> ReadBodyAsync(int contentLength, CancellationToken ct)
    {
        if (contentLength <= 0)
        {
            return [];
        }

        var body = new byte[contentLength];
        var fromPending = TakeFromPending(Math.Min(_pending.Length, contentLength));
        fromPending.CopyTo(body.AsSpan());

        var offset = fromPending.Length;
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

    /// <summary>
    /// Decodes a <c>Transfer-Encoding: chunked</c> request body into a single buffer.
    /// Consumes the terminating zero-length chunk and any trailer section (dropping the
    /// trailers), leaving the reader positioned at the next pipelined request.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A chunk size line is malformed, a chunk is not followed by CRLF, or the stream
    /// ends before the body is complete.
    /// </exception>
    /// <remarks>
    /// <code>
    ///   ┌─ "1a;ext=v\r\n"      chunk-size [;extensions]
    ///   ├─ &lt;1a bytes&gt;"\r\n"     chunk data + CRLF
    ///   ├─ ...                 (repeat)
    ///   ├─ "0\r\n"             last chunk (size 0)
    ///   ├─ "X: y\r\n"          optional trailer headers (consumed, dropped)
    ///   └─ "\r\n"              terminating blank line
    /// </code>
    /// </remarks>
    public async Task<byte[]> ReadChunkedBodyAsync(CancellationToken ct)
    {
        var body = new List<byte>();

        while (true)
        {
            var sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
            var semicolon = sizeLine.IndexOf(';', StringComparison.Ordinal);
            var hex = (semicolon >= 0 ? sizeLine[..semicolon] : sizeLine).Trim();
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new InvalidOperationException($"Malformed chunk size: '{sizeLine}'.");
            }

            if (size == 0)
            {
                // Last chunk: consume the (possibly empty) trailer section up to the
                // terminating blank line. Trailers are dropped — the body is re-framed
                // with Content-Length on forward, so they have nowhere to go.
                while ((await ReadLineAsync(ct).ConfigureAwait(false)).Length > 0)
                {
                }
                break;
            }

            var chunk = await ReadExactlyAsync(size, ct).ConfigureAwait(false);
            body.AddRange(chunk);

            var crlf = await ReadExactlyAsync(2, ct).ConfigureAwait(false);
            if (crlf[0] != (byte)'\r' || crlf[1] != (byte)'\n')
            {
                throw new InvalidOperationException("Missing CRLF after chunk data.");
            }
        }

        return [.. body];
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, throwing on premature EOF.</summary>
    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        if (count == 0)
        {
            return [];
        }

        while (_pending.Length < count)
        {
            if (await PullAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading chunked body.");
            }
        }

        return TakeFromPending(count);
    }

    /// <summary>
    /// Reads a single CRLF-terminated line (without the CRLF). Used for chunk-size and
    /// trailer lines, which are small — capped at <see cref="MaxChunkLineBytes"/>.
    /// </summary>
    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var scanFrom = 0;
        while (true)
        {
            var crlf = IndexOfCrlf(_pending, scanFrom);
            if (crlf >= 0)
            {
                var line = Encoding.ASCII.GetString(_pending, 0, crlf);
                _pending = _pending.Length > crlf + 2 ? _pending[(crlf + 2)..] : [];
                return line;
            }

            if (_pending.Length > MaxChunkLineBytes)
            {
                throw new InvalidOperationException("Chunk header line too large.");
            }

            // A CRLF can straddle two reads, so resume the scan one byte back.
            scanFrom = Math.Max(0, _pending.Length - 1);
            if (await PullAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading chunk header.");
            }
        }
    }

    /// <summary>Reads one block from the stream and appends it to <c>_pending</c>; returns bytes read.</summary>
    private async Task<int> PullAsync(CancellationToken ct)
    {
        var buffer = new byte[ReadChunkBytes];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return 0;
        }

        var combined = new byte[_pending.Length + read];
        Array.Copy(_pending, combined, _pending.Length);
        Array.Copy(buffer, 0, combined, _pending.Length, read);
        _pending = combined;
        return read;
    }

    /// <summary>Removes and returns the first <paramref name="count"/> bytes of <c>_pending</c>.</summary>
    private byte[] TakeFromPending(int count)
    {
        if (count == 0)
        {
            return [];
        }

        var taken = _pending[..count];
        _pending = _pending.Length > count ? _pending[count..] : [];
        return taken;
    }

    private static int IndexOfCrlf(byte[] data, int start)
    {
        for (var i = Math.Max(0, start); i + 1 < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static byte[] Slice(List<byte> accumulator, int start) =>
        start >= accumulator.Count ? [] : accumulator.GetRange(start, accumulator.Count - start).ToArray();
}
