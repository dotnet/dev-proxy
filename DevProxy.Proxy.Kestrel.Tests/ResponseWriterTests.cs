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
    private static async Task<string> WriteAsync(MutableHttpResponse response, bool keepAlive = false, string method = "GET")
    {
        using var stream = new MemoryStream();
        await ResponseWriter.WriteAsync(stream, response, keepAlive, method, CancellationToken.None);
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
    public async Task WriteContinueAsync_WritesInterim100()
    {
        using var stream = new MemoryStream();

        await ResponseWriter.WriteContinueAsync(stream, CancellationToken.None);

        Assert.Equal("HTTP/1.1 100 Continue\r\n\r\n", Encoding.ASCII.GetString(stream.ToArray()));
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
        // The incoming hop-by-hop Connection header is stripped; the writer emits its
        // own based on the keepAlive flag (false here).
        Assert.Contains("Connection: close\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WritesConnectionClose_WhenNotKeepAlive()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), Encoding.ASCII.GetBytes("x"));

        var output = await WriteAsync(response, keepAlive: false);

        Assert.Contains("Connection: close\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: keep-alive", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WritesConnectionKeepAlive_WhenKeepAlive()
    {
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), Encoding.ASCII.GetBytes("x"));

        var output = await WriteAsync(response, keepAlive: true);

        Assert.Contains("Connection: keep-alive\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: close", output, StringComparison.Ordinal);
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

    [Fact]
    public async Task WriteAsync_Head_PreservesOriginContentLength_AndWritesNoBody()
    {
        // A HEAD response carries the Content-Length a GET would return but no body
        // (RFC 9110 §9.3.2). The origin's declared length must survive, not be
        // overwritten with the (empty) body length.
        var headers = new HeaderCollection();
        headers.Add("Content-Length", "1234");
        headers.Add("Content-Type", "application/json");
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty);

        var output = await WriteAsync(response, method: "HEAD");

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", output, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 1234\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length: 0\r\n", output, StringComparison.Ordinal);
        // The head ends at the blank line and nothing follows it (no body).
        Assert.EndsWith("\r\n\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_Head_SuppressesBody_EvenIfResponseCarriesOne()
    {
        // Defensive: if a plugin attaches a body to a HEAD response, the bytes must
        // still not reach the client, but Content-Length reflects that body's size.
        var headers = new HeaderCollection();
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, headers, Encoding.ASCII.GetBytes("hello"));

        var output = await WriteAsync(response, method: "HEAD");

        Assert.Contains("Content-Length: 5\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", output, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_Head_FallsBackToBodyLength_WhenNoOriginContentLength()
    {
        // No Content-Length from the origin (e.g. it was chunked or compressed-then-
        // stripped). Fall back to the body length rather than omitting the header.
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

        var output = await WriteAsync(response, method: "HEAD");

        Assert.Contains("Content-Length: 0\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_Get_RecomputesContentLength_IgnoringOrigin()
    {
        // The HEAD preservation must not leak into other methods: a GET still gets a
        // recomputed Content-Length from the actual body.
        var headers = new HeaderCollection();
        headers.Add("Content-Length", "1234");
        var response = new MutableHttpResponse(
            HttpStatusCode.OK, HttpVersion.Version11, headers, Encoding.ASCII.GetBytes("abc"));

        var output = await WriteAsync(response, method: "GET");

        Assert.Contains("Content-Length: 3\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length: 1234", output, StringComparison.Ordinal);
        Assert.EndsWith("abc", output, StringComparison.Ordinal);
    }
}
