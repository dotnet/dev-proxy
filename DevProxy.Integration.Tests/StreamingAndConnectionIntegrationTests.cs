// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Integration scenarios for streaming bodies, chunked request framing, and connection reuse —
/// all over plain HTTP so they need no TLS trust.
/// </summary>
public sealed class StreamingAndConnectionIntegrationTests
{
    [Fact]
    public async Task Sse_IsRelayedChunked_WithAllEvents()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(
            new Uri($"http://{origin.Host}/sse"),
            HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The engine re-frames the streamed origin body as HTTP/1.1 chunked.
        Assert.True(response.Headers.TransferEncodingChunked ?? false);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        for (var i = 0; i < 5; i++)
        {
            Assert.Contains($"data: event-{i}", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChunkedRequestBody_IsReframedAndReachesOrigin()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        // No Content-Length + chunked transfer-encoding → HttpClient streams chunked.
        using var content = new StreamContent(
            new MemoryStream(Encoding.UTF8.GetBytes("chunked-payload")));
        content.Headers.ContentLength = null;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"http://{origin.Host}/echo"))
        {
            Content = content,
        };
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chunked-payload", body);
    }

    [Fact]
    public async Task KeepAlive_TwoRequestsSucceed_ResponseAdvertisesKeepAlive()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var first = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        using var second = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("hello get", await second.Content.ReadAsStringAsync());
        Assert.Contains(
            first.Headers.Connection,
            v => string.Equals(v, "keep-alive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectionClose_IsHonoured()
    {
        await using var origin = await FakeOrigin.StartAsync();
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host);
        using var client = proxy.CreateHttpClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://{origin.Host}/get"));
        request.Headers.ConnectionClose = true;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello get", await response.Content.ReadAsStringAsync());
    }
}
