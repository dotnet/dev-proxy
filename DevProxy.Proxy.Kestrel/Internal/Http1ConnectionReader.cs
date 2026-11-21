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
    internal const int MaxBufferedBodyBytes = 256 * 1024 * 1024;
    private const int ReadChunkBytes = 4096;
    private const int MaxChunkLineBytes = 64 * 1024;

    // Bytes read off the stream but not yet consumed live in _buffer[_start.._end].
    // Consuming advances _start; reading appends at _end, compacting or growing the
    // buffer only when there is no room left. Chunk data never passes through here
    // in bulk (it is copied straight into the body), so the buffer stays small.
    private byte[] _buffer = [];
    private int _start;
    private int _end;

    private int PendingLength => _end - _start;

    private ReadOnlySpan<byte> Pending => _buffer.AsSpan(_start, PendingLength);

    /// <summary>
    /// Reads the next request head, or <c>null</c> on a clean EOF (the peer closed the
    /// connection between requests, or mid-headers before a complete block arrived).
    /// </summary>
    public async Task<ParsedRequestHead?> ReadHeadAsync(CancellationToken ct)
    {
        var accumulator = new List<byte>(PendingLength);
        accumulator.AddRange(Pending);
        Consume(PendingLength);
        var buffer = new byte[ReadChunkBytes];

        while (true)
        {
            var terminator = Http1RequestReader.IndexOfDoubleCrlf(accumulator);
            if (terminator >= 0)
            {
                var headerText = Encoding.ASCII.GetString(accumulator.ToArray(), 0, terminator);
                SetPending(Slice(accumulator, terminator + 4));
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
    /// A premature EOF is rejected as invalid framing.
    /// </summary>
    public async Task<byte[]> ReadBodyAsync(int contentLength, CancellationToken ct)
    {
        if (contentLength <= 0)
        {
            return [];
        }
        if (contentLength > MaxBufferedBodyBytes)
        {
            throw new RequestBodyTooLargeException();
        }

        var body = new byte[contentLength];
        var offset = TakeFromPending(body);
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading request body.");
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
        using var body = new BodyAccumulator();

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

            if (body.Length > MaxBufferedBodyBytes - size)
            {
                throw new RequestBodyTooLargeException();
            }
            await ReadChunkDataAsync(body, size, ct).ConfigureAwait(false);

            await EnsurePendingAsync(2, ct).ConfigureAwait(false);
            if (_buffer[_start] != (byte)'\r' || _buffer[_start + 1] != (byte)'\n')
            {
                throw new InvalidOperationException("Missing CRLF after chunk data.");
            }
            Consume(2);
        }

        return body.ToArray();
    }

    /// <summary>
    /// Copies exactly <paramref name="count"/> bytes of chunk data into
    /// <paramref name="body"/>: buffered bytes first, then straight from the stream.
    /// Throws on premature EOF.
    /// </summary>
    private async Task ReadChunkDataAsync(BodyAccumulator body, int count, CancellationToken ct)
    {
        var fromPending = Math.Min(PendingLength, count);
        body.Write(_buffer.AsSpan(_start, fromPending));
        Consume(fromPending);

        var remaining = count - fromPending;
        while (remaining > 0)
        {
            var target = body.GetMemory();
            if (target.Length > remaining)
            {
                target = target[..remaining];
            }
            var read = await stream.ReadAsync(target, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading chunked body.");
            }
            body.Advance(read);
            remaining -= read;
        }
    }

    /// <summary>Ensures at least <paramref name="count"/> bytes are buffered, throwing on premature EOF.</summary>
    private async Task EnsurePendingAsync(int count, CancellationToken ct)
    {
        while (PendingLength < count)
        {
            if (await PullAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading chunked body.");
            }
        }
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
            var crlf = IndexOfCrlf(Pending, scanFrom);
            if (crlf >= 0)
            {
                var line = Encoding.ASCII.GetString(_buffer, _start, crlf);
                Consume(crlf + 2);
                return line;
            }

            if (PendingLength > MaxChunkLineBytes)
            {
                throw new InvalidOperationException("Chunk header line too large.");
            }

            // A CRLF can straddle two reads, so resume the scan one byte back.
            scanFrom = Math.Max(0, PendingLength - 1);
            if (await PullAsync(ct).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("Unexpected end of stream while reading chunk header.");
            }
        }
    }

    /// <summary>Reads one block from the stream and appends it to the pending bytes; returns bytes read.</summary>
    private async Task<int> PullAsync(CancellationToken ct)
    {
        if (_buffer.Length - _end < ReadChunkBytes)
        {
            var pending = PendingLength;
            if (_buffer.Length - pending >= ReadChunkBytes)
            {
                // Enough room once consumed bytes are dropped: slide the pending bytes down.
                _buffer.AsSpan(_start, pending).CopyTo(_buffer);
            }
            else
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, pending + ReadChunkBytes)];
                _buffer.AsSpan(_start, pending).CopyTo(grown);
                _buffer = grown;
            }
            _start = 0;
            _end = pending;
        }

        // Bounded so ReadLineAsync checks MaxChunkLineBytes at the same granularity
        // as before; bulk chunk data bypasses this path.
        var read = await stream.ReadAsync(_buffer.AsMemory(_end, ReadChunkBytes), ct).ConfigureAwait(false);
        _end += read;
        return read;
    }

    /// <summary>Moves up to <paramref name="destination"/>.Length pending bytes into it; returns the count.</summary>
    private int TakeFromPending(Span<byte> destination)
    {
        var count = Math.Min(PendingLength, destination.Length);
        _buffer.AsSpan(_start, count).CopyTo(destination);
        Consume(count);
        return count;
    }

    private void Consume(int count)
    {
        _start += count;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
    }

    private void SetPending(byte[] data)
    {
        _buffer = data;
        _start = 0;
        _end = data.Length;
    }

    private static int IndexOfCrlf(ReadOnlySpan<byte> data, int start)
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

internal sealed class RequestBodyTooLargeException : InvalidOperationException
{
    public RequestBodyTooLargeException()
        : base("Request body too large.")
    {
    }

    public RequestBodyTooLargeException(string message)
        : base(message)
    {
    }

    public RequestBodyTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
