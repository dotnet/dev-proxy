// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class Http1RequestReaderTests
{
    [Fact]
    public async Task ReadHeadAsync_ParsesRequestLineAndHeaders()
    {
        const string raw =
            "GET /posts/1 HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Accept: application/json\r\n" +
            "\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

        var head = await Http1RequestReader.ReadHeadAsync(stream, CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("GET", head!.Method);
        Assert.Equal("/posts/1", head.Target);
        Assert.Equal("HTTP/1.1", head.Version);
        Assert.Contains(head.Headers, h => h.Name == "Host" && h.Value == "example.com");
        Assert.Contains(head.Headers, h => h.Name == "Accept" && h.Value == "application/json");
    }

    [Fact]
    public async Task ReadHeadAsync_ReturnsNull_OnCleanEofBeforeAnyBytes()
    {
        using var stream = new MemoryStream([]);

        var head = await Http1RequestReader.ReadHeadAsync(stream, CancellationToken.None);

        Assert.Null(head);
    }

    [Fact]
    public async Task ReadHeadAsync_Throws_OnMalformedRequestLine()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("GARBAGE\r\n\r\n"));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Http1RequestReader.ReadHeadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void GetContentLength_ReadsHeaderCaseInsensitively()
    {
        var headers = new List<(string Name, string Value)>
        {
            ("Host", "example.com"),
            ("content-length", "42"),
        };

        Assert.Equal(42, Http1RequestReader.GetContentLength(headers));
    }

    [Fact]
    public void GetContentLength_DefaultsToZero_WhenAbsent()
    {
        var headers = new List<(string Name, string Value)> { ("Host", "example.com") };

        Assert.Equal(0, Http1RequestReader.GetContentLength(headers));
    }

    [Fact]
    public async Task ReadBodyAsync_UsesLeftoverThenStream()
    {
        // The header read consumed "AB" past the terminator; the rest comes from the stream.
        var leftover = Encoding.ASCII.GetBytes("AB");
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("CDE"));

        var body = await Http1RequestReader.ReadBodyAsync(stream, leftover, contentLength: 5, CancellationToken.None);

        Assert.Equal("ABCDE", Encoding.ASCII.GetString(body));
    }

    [Fact]
    public async Task ReadBodyAsync_ReturnsEmpty_WhenContentLengthZero()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ignored"));

        var body = await Http1RequestReader.ReadBodyAsync(stream, [], contentLength: 0, CancellationToken.None);

        Assert.Empty(body);
    }

    [Fact]
    public async Task ReadHeadAsync_LeftoverContainsBodyBytesReadWithHeaderBlock()
    {
        const string raw =
            "POST /posts HTTP/1.1\r\n" +
            "Content-Length: 3\r\n" +
            "\r\n" +
            "abc";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

        var head = await Http1RequestReader.ReadHeadAsync(stream, CancellationToken.None);
        Assert.NotNull(head);

        var length = Http1RequestReader.GetContentLength(head!.Headers);
        var body = await Http1RequestReader.ReadBodyAsync(stream, head.Leftover, length, CancellationToken.None);

        Assert.Equal("abc", Encoding.ASCII.GetString(body));
    }
}
