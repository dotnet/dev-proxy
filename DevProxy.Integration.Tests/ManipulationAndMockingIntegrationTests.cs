// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
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
