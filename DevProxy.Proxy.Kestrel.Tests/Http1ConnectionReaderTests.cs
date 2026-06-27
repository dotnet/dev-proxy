// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class Http1ConnectionReaderTests
{
    [Fact]
    public async Task ReadHeadAsync_ParsesRequestLineAndHeaders()
    {
        var reader = ReaderOver(
            "GET /posts/1 HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "\r\n");

        var head = await reader.ReadHeadAsync(CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("GET", head!.Method);
        Assert.Equal("/posts/1", head.Target);
        Assert.Contains(head.Headers, h => h.Name == "Host" && h.Value == "example.com");
    }

    [Fact]
    public async Task ReadHeadAsync_ReturnsNull_OnCleanEof()
    {
        var reader = ReaderOver("");

        Assert.Null(await reader.ReadHeadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadHeadAsync_ReturnsNull_OnTruncatedHeaderBlock()
    {
        // Connection closed before the terminating CRLFCRLF arrived.
        var reader = ReaderOver("GET / HTTP/1.1\r\nHost: x\r\n");

        Assert.Null(await reader.ReadHeadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadHeadAsync_Throws_OnMalformedRequestLine()
    {
        var reader = ReaderOver("GARBAGE\r\n\r\n");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadHeadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadBodyAsync_ReadsContentLengthBody()
    {
        var reader = ReaderOver(
            "POST /posts HTTP/1.1\r\n" +
            "Content-Length: 3\r\n" +
            "\r\n" +
            "abc");

        var head = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadBodyAsync(
            Http1RequestReader.GetContentLength(head!.Headers), CancellationToken.None);

        Assert.Equal("abc", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadBodyAsync_ReturnsEmpty_WhenContentLengthZero()
    {
        var reader = ReaderOver("GET / HTTP/1.1\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadBodyAsync(0, CancellationToken.None);

        Assert.Empty(body);
    }

    [Fact]
    public async Task KeepAlive_ReadsTwoPipelinedRequestsFromOneBuffer()
    {
        // Both requests (with bodies) arrive back-to-back in a single buffer. The
        // reader must frame each one exactly, retaining request 2's bytes while it
        // serves request 1. This is the core keep-alive/pipelining correctness case.
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nContent-Length: 2\r\n\r\nAA" +
            "POST /b HTTP/1.1\r\nContent-Length: 3\r\n\r\nBBB",
            // Fragment aggressively to prove cross-read accumulation works.
            maxBytesPerRead: 5);

        var first = await reader.ReadHeadAsync(CancellationToken.None);
        Assert.Equal("/a", first!.Target);
        var firstBody = await reader.ReadBodyAsync(2, CancellationToken.None);
        Assert.Equal("AA", Encoding.ASCII.GetString(firstBody));

        var second = await reader.ReadHeadAsync(CancellationToken.None);
        Assert.Equal("/b", second!.Target);
        var secondBody = await reader.ReadBodyAsync(3, CancellationToken.None);
        Assert.Equal("BBB", Encoding.ASCII.GetString(secondBody));

        Assert.Null(await reader.ReadHeadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadBodyAsync_RetainsSurplusBytesForNextRequest()
    {
        // The header read over-reads into request 2; ReadBodyAsync must consume exactly
        // Content-Length and leave the surplus for the next ReadHeadAsync.
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nContent-Length: 1\r\n\r\nX" +
            "GET /b HTTP/1.1\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadBodyAsync(1, CancellationToken.None);
        Assert.Equal("X", Encoding.ASCII.GetString(body));

        var next = await reader.ReadHeadAsync(CancellationToken.None);
        Assert.Equal("/b", next!.Target);
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_DecodesSingleChunk()
    {
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n0\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadChunkedBodyAsync(CancellationToken.None);

        Assert.Equal("hello", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_DecodesMultipleChunks()
    {
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadChunkedBodyAsync(CancellationToken.None);

        Assert.Equal("hello world", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_IgnoresChunkExtensions()
    {
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5;name=value\r\nhello\r\n0\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadChunkedBodyAsync(CancellationToken.None);

        Assert.Equal("hello", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_ConsumesTrailers_AndRetainsSurplus()
    {
        // Trailers after the 0-length chunk are consumed (dropped), and a pipelined
        // next request that arrives right after the terminating blank line survives.
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "4\r\ndata\r\n0\r\nX-Checksum: abc123\r\n\r\n" +
            "GET /b HTTP/1.1\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadChunkedBodyAsync(CancellationToken.None);
        Assert.Equal("data", Encoding.ASCII.GetString(body));

        var next = await reader.ReadHeadAsync(CancellationToken.None);
        Assert.Equal("/b", next!.Target);
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_DecodesUnderAggressiveFragmentation()
    {
        // One byte per read forces every CRLF and chunk boundary to straddle reads,
        // exercising the cross-read line/exact accumulation.
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n",
            maxBytesPerRead: 1);

        _ = await reader.ReadHeadAsync(CancellationToken.None);
        var body = await reader.ReadChunkedBodyAsync(CancellationToken.None);

        Assert.Equal("hello world", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_Throws_OnMalformedChunkSize()
    {
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "zz\r\nhello\r\n0\r\n\r\n");

        _ = await reader.ReadHeadAsync(CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadChunkedBodyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadChunkedBodyAsync_Throws_OnTruncatedBody()
    {
        // The stream ends before the declared 9 bytes (and terminating chunk) arrive.
        var reader = ReaderOver(
            "POST /a HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "9\r\nhello");

        _ = await reader.ReadHeadAsync(CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadChunkedBodyAsync(CancellationToken.None));
    }

    private static Http1ConnectionReader ReaderOver(string raw, int maxBytesPerRead = int.MaxValue) =>
        new(new ChunkedStream(Encoding.ASCII.GetBytes(raw), maxBytesPerRead));

    // A read-only stream that returns at most maxBytesPerRead bytes per ReadAsync,
    // simulating TCP segmentation so the reader's cross-read accumulation is exercised.
    private sealed class ChunkedStream(byte[] data, int maxBytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = data.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }
            var toCopy = Math.Min(Math.Min(remaining, buffer.Length), maxBytesPerRead);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
