// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class ResponseWriterTests
{
    private static async Task<string> WriteAsync(MutableHttpResponse response)
    {
        using var stream = new MemoryStream();
        await ResponseWriter.WriteAsync(stream, response, CancellationToken.None);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public async Task WriteAsync_WritesStatusLineAndBody()
    {
        var headers = new HeaderCollection();
        headers.Add("Content-Type", "text/plain");
        var body = Encoding.ASCII.GetBytes("hello");
        var response = new MutableHttpResponse(HttpStatusCode.OK, HttpVersion.Version11, headers, body);

        var output = await WriteAsync(response);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", output, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain\r\n", output, StringComparison.Ordinal);
        Assert.EndsWith("hello", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_RecomputesContentLengthFromBody()
    {
        var headers = new HeaderCollection();
        // A stale Content-Length must be replaced with the actual body length.
        headers.Add("Content-Length", "999");
        var body = Encoding.ASCII.GetBytes("12345");
        var response = new MutableHttpResponse(HttpStatusCode.OK, HttpVersion.Version11, headers, body);

        var output = await WriteAsync(response);

        Assert.Contains("Content-Length: 5\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length: 999", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_StripsHopByHopAndContentEncodingHeaders()
    {
        var headers = new HeaderCollection();
        headers.Add("Content-Encoding", "gzip");
        headers.Add("Transfer-Encoding", "chunked");
        headers.Add("Connection", "keep-alive");
        headers.Add("X-Custom", "keep-me");
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, headers, Encoding.ASCII.GetBytes("body"));

        var output = await WriteAsync(response);

        Assert.DoesNotContain("Content-Encoding", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Transfer-Encoding", output, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-alive", output, StringComparison.Ordinal);
        Assert.Contains("X-Custom: keep-me\r\n", output, StringComparison.Ordinal);
        // The engine always closes the connection in slice 1.
        Assert.Contains("Connection: close\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_UsesReasonPhraseForStatusCode()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.NotFound, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

        var output = await WriteAsync(response);

        Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", output, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 0\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_PrefersExplicitStatusDescription()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty,
            statusDescription: "Totally Fine");

        var output = await WriteAsync(response);

        Assert.StartsWith("HTTP/1.1 200 Totally Fine\r\n", output, StringComparison.Ordinal);
    }
}
