// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class UpstreamForwarderTests
{
    private static MutableHttpRequest Request(string method = "GET") =>
        new(method, new Uri("https://origin.test/sse"), HttpVersion.Version11, new HeaderCollection(), ReadOnlyMemory<byte>.Empty);

    private static UpstreamForwarder ForwarderReturning(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)));

    [Fact]
    public async Task ForwardAsync_EventStream_IsStreaming_WithOpenBody()
    {
        var content = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes("data: 1\n\n")));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await using var origin = await ForwarderReturning(message).ForwardAsync(Request(), CancellationToken.None);

        Assert.True(origin.IsStreaming);
        Assert.NotNull(origin.BodyStream);
        Assert.False(origin.Response.HasBody);

        using var reader = new StreamReader(origin.BodyStream!);
        Assert.Equal("data: 1\n\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ForwardAsync_NonStreaming_IsBuffered()
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("hello"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await using var origin = await ForwarderReturning(message).ForwardAsync(Request(), CancellationToken.None);

        Assert.False(origin.IsStreaming);
        Assert.Null(origin.BodyStream);
        Assert.Equal("hello", origin.Response.BodyString);
    }

    [Fact]
    public async Task ForwardAsync_StripsFramingHeaders()
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("x"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await using var origin = await ForwarderReturning(message).ForwardAsync(Request(), CancellationToken.None);

        Assert.Null(origin.Response.Headers.GetFirst("Transfer-Encoding"));
        Assert.Null(origin.Response.Headers.GetFirst("Content-Encoding"));
        // Content-Length is recomputed on write-back, not carried from the origin.
        Assert.Null(origin.Response.Headers.GetFirst("Content-Length"));
    }

    [Fact]
    public async Task ForwardAsync_Head_PreservesContentLength()
    {
        // A HEAD response has no body but reports the resource's Content-Length; it
        // must survive forwarding so the client sees the real size (RFC 9110 §9.3.2).
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 4096;
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await using var origin = await ForwarderReturning(message).ForwardAsync(Request("HEAD"), CancellationToken.None);

        Assert.False(origin.IsStreaming);
        Assert.Equal("4096", origin.Response.Headers.GetFirst("Content-Length")?.Value);
    }

    [Fact]
    public async Task ForwardAsync_Head_EventStream_IsNotStreaming()
    {
        // A HEAD to an SSE endpoint has no body to stream — it must take the buffered
        // path, never the streaming one.
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await using var origin = await ForwarderReturning(message).ForwardAsync(Request("HEAD"), CancellationToken.None);

        Assert.False(origin.IsStreaming);
        Assert.Null(origin.BodyStream);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
