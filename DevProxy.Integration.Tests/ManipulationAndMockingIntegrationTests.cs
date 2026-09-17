// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using DevProxy.Plugins.Behavior;
using DevProxy.Plugins.Manipulation;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Per-plugin integration coverage for the request-manipulation and mocking plugins,
/// proving each reshapes the request/response correctly through the Kestrel engine.
/// </summary>
public sealed class ManipulationAndMockingIntegrationTests
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TestProxyConfiguration ProxyConfig = new();

    [Fact]
    public async Task Rewrite_RewritesRequestUrl_OriginServesRewrittenPath()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        // Rewrite /get → /status/503 so the origin serves the rewritten path.
        var config = PluginConfig.FromJson("""
            {
              "rewrites": [
                { "in": { "url": "/get$" }, "out": { "url": "/status/503" } }
              ]
            }
            """);
        var plugin = new RewritePlugin(
            SharedHttpClient,
            NullLogger<RewritePlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        // 503 proves the request was rewritten to /status/503 before forwarding.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Rewrite_RewritesWebSocketUrl_OriginReceivesRewrittenPath()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson("""
            {
              "rewrites": [
                { "in": { "url": "/socket-a$" }, "out": { "url": "/socket-b" } }
              ]
            }
            """);
        var plugin = new RewritePlugin(
            SharedHttpClient,
            NullLogger<RewritePlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port, cts.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://{origin.Host}/socket-a HTTP/1.1\r\n" +
            $"Host: {origin.Host}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n"), cts.Token);
        await stream.FlushAsync(cts.Token);
        var responseHead = await ReadUntilDoubleCrlfAsync(stream, cts.Token);

        Assert.Contains(origin.ReceivedRequests, request => request.PathAndQuery == "/socket-b");
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", responseHead, StringComparison.Ordinal);
    }

    private static async Task<string> ReadUntilDoubleCrlfAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4
                && bytes[^4] == (byte)'\r' && bytes[^3] == (byte)'\n'
                && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [Fact]
    public async Task MockResponse_ShortCircuitsWithConfiguredMock()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 201, "body": "mocked-by-test" }
                }
              ]
            }
            """);
        var plugin = new MockResponsePlugin(
            SharedHttpClient,
            NullLogger<MockResponsePlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        var body = await response.Content.ReadAsStringAsync();

        // /get normally returns 200 "hello get"; the mock overrides it.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("mocked-by-test", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_ApiKey_RejectsRequestWithoutKey()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson("""
            {
              "type": "apiKey",
              "apiKey": {
                "allowedKeys": [ "secret-key" ],
                "parameters": [ { "in": "header", "name": "x-api-key" } ]
              }
            }
            """);
        var plugin = new AuthPlugin(
            SharedHttpClient,
            NullLogger<AuthPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        // No x-api-key header ⇒ 401 before the request ever reaches the origin.
        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_ApiKey_AllowsRequestWithValidKey()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson("""
            {
              "type": "apiKey",
              "apiKey": {
                "allowedKeys": [ "secret-key" ],
                "parameters": [ { "in": "header", "name": "x-api-key" } ]
              }
            }
            """);
        var plugin = new AuthPlugin(
            SharedHttpClient,
            NullLogger<AuthPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://{origin.Host}/get"));
        request.Headers.Add("x-api-key", "secret-key");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello get", body);
    }

    [Fact]
    public async Task GraphRandomError_Rate100_FailsEveryMatchedRequest()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson("""{ "rate": 100 }""");
        var plugin = new GraphRandomErrorPlugin(
            SharedHttpClient,
            NullLogger<GraphRandomErrorPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        // rate:100 ⇒ always an injected error status; never the origin's 200.
        Assert.True(
            (int)response.StatusCode >= 400,
            $"Expected an injected 4xx/5xx error, saw {(int)response.StatusCode}.");
    }
}
