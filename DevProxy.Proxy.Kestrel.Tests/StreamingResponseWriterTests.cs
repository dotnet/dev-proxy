// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class StreamingResponseWriterTests
{
    private static MutableHttpResponse Response(params (string Name, string Value)[] headers)
    {
        var collection = new HeaderCollection();
        foreach (var (name, value) in headers)
        {
            collection.Add(name, value);
        }
        return new MutableHttpResponse(HttpStatusCode.OK, HttpVersion.Version11, collection, ReadOnlyMemory<byte>.Empty);
    }

    private static byte[] Seg(string text) => Encoding.ASCII.GetBytes(text);

    private static async Task<(string Raw, ReadOnlyMemory<byte> Accumulated)> WriteAsync(
        MutableHttpResponse response, IReadOnlyList<byte[]> segments, bool keepAlive = true, int accumulateCap = 1024)
    {
        using var client = new MemoryStream();
        using var origin = new ScriptedReadStream(segments);
        var accumulated = await StreamingResponseWriter.WriteAsync(
            client, response, origin, keepAlive, accumulateCap, CancellationToken.None);
        return (Encoding.ASCII.GetString(client.ToArray()), accumulated);
    }

    // Splits the wire output into head and the decoded chunked body.
    private static (string Head, byte[] Body) Parse(string raw)
    {
        var marker = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = raw[..(marker + 4)];
        var rest = Encoding.ASCII.GetBytes(raw[(marker + 4)..]);

        var body = new List<byte>();
        var pos = 0;
        while (true)
        {
            var lineEnd = IndexOfCrlf(rest, pos);
            var size = int.Parse(Encoding.ASCII.GetString(rest, pos, lineEnd - pos), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pos = lineEnd + 2;
            if (size == 0)
            {
                break;
            }
            body.AddRange(rest[pos..(pos + size)]);
            pos += size + 2; // data + trailing CRLF
        }
        return (head, [.. body]);
    }

    private static int IndexOfCrlf(byte[] data, int start)
    {
        for (var i = start; i < data.Length - 1; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }
        throw new InvalidOperationException("CRLF not found.");
    }

    [Fact]
    public async Task WriteAsync_EmitsChunkedHead_NoContentLength()
    {
        var response = Response(("Content-Type", "text/event-stream"));

        var (raw, _) = await WriteAsync(response, [Seg("data: 1\n\n")]);
        var (head, _) = Parse(raw);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/event-stream\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked\r\n", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", head, StringComparison.Ordinal);
        Assert.Contains("Connection: keep-alive\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_KeepAliveFalse_EmitsConnectionClose()
    {
        var (raw, _) = await WriteAsync(Response(), [Seg("x")], keepAlive: false);
        var (head, _) = Parse(raw);

        Assert.Contains("Connection: close\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_ForwardsEachSegmentAsItsOwnChunk()
    {
        // Two distinct SSE events arrive in two reads → two chunks on the wire.
        var (raw, _) = await WriteAsync(Response(), [Seg("data: a\n\n"), Seg("data: bb\n\n")]);

        // "data: a\n\n" is 9 bytes (0x9), "data: bb\n\n" is 10 bytes (0xA).
        Assert.Contains("9\r\ndata: a\n\n\r\n", raw, StringComparison.Ordinal);
        Assert.Contains("A\r\ndata: bb\n\n\r\n", raw, StringComparison.Ordinal);
        Assert.EndsWith("0\r\n\r\n", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_DecodedBody_EqualsConcatenatedSegments()
    {
        var (raw, _) = await WriteAsync(Response(), [Seg("hello "), Seg("world")]);
        var (_, body) = Parse(raw);

        Assert.Equal("hello world", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task WriteAsync_AccumulatesFullBody_WhenUnderCap()
    {
        var (_, accumulated) = await WriteAsync(Response(), [Seg("abc"), Seg("def")], accumulateCap: 1024);

        Assert.Equal("abcdef", Encoding.ASCII.GetString(accumulated.Span));
    }

    [Fact]
    public async Task WriteAsync_DropsAccumulation_WhenOverCap_ButStillRelaysFullBody()
    {
        // cap=4, body=6 bytes → accumulation abandoned, but the client still gets it all.
        var (raw, accumulated) = await WriteAsync(Response(), [Seg("abc"), Seg("def")], accumulateCap: 4);
        var (_, body) = Parse(raw);

        Assert.True(accumulated.IsEmpty);
        Assert.Equal("abcdef", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task WriteAsync_NoAccumulation_WhenCapZero()
    {
        var (raw, accumulated) = await WriteAsync(Response(), [Seg("abc")], accumulateCap: 0);
        var (_, body) = Parse(raw);

        Assert.True(accumulated.IsEmpty);
        Assert.Equal("abc", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task WriteAsync_StripsFramingAndEncodingHeaders()
    {
        var response = Response(
            ("Content-Length", "99"),
            ("Content-Encoding", "gzip"),
            ("Transfer-Encoding", "chunked"),
            ("Cache-Control", "no-cache"));

        var (raw, _) = await WriteAsync(response, [Seg("x")]);
        var (head, _) = Parse(raw);

        Assert.DoesNotContain("Content-Length: 99", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Encoding", head, StringComparison.Ordinal);
        // Exactly one Transfer-Encoding (the chunked one we add), not the origin's.
        Assert.Equal("no-cache", response.Headers.GetFirst("Cache-Control")!.Value);
        Assert.Contains("Cache-Control: no-cache\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_EmptyBody_WritesOnlyTerminatingChunk()
    {
        var (raw, accumulated) = await WriteAsync(Response(), []);
        var (_, body) = Parse(raw);

        Assert.Empty(body);
        Assert.True(accumulated.IsEmpty);
        Assert.EndsWith("0\r\n\r\n", raw, StringComparison.Ordinal);
    }
}
