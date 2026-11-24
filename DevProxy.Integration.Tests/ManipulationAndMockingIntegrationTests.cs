// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevProxy.Abstractions.Proxy;
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

    [Theory]
    [InlineData("original")]
    [InlineData(null)]
    public async Task Rewrite_SetsRequestHeader_WhenUrlMatches(string? originalValue)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson("""
            {
              "rewrites": [
                { "in": { "url": "/headers$" }, "out": { "headers": [ { "name": "x-probe", "value": "rewritten" } ] } }
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
        using var client = proxy.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{origin.Host}/headers"));
        if (originalValue is not null)
        {
            request.Headers.Add("X-Probe", originalValue);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("probe=rewritten", body);
        Assert.Contains(origin.ReceivedRequests, r => r.PathAndQuery == "/headers");
    }

    [Fact]
    public async Task Rewrite_RewritesUrlAndHeader_MatchingHeaderAgainstOriginalUrl()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson("""
            {
              "rewrites": [
                { "in": { "url": "/get$" }, "out": { "url": "/headers", "headers": [ { "name": "X-Probe", "value": "rewritten" } ] } }
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
        using var client = proxy.CreateHttpClient();

        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(origin.ReceivedRequests, r => r.PathAndQuery == "/headers");
        Assert.Equal("probe=rewritten", body);
    }

    [Fact]
    public async Task Rewrite_LeavesRequestHeader_WhenUrlDoesNotMatch()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson("""
            {
              "rewrites": [
                { "in": { "url": "/other$" }, "out": { "headers": [ { "name": "X-Probe", "value": "rewritten" } ] } }
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
        using var client = proxy.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{origin.Host}/headers"));
        request.Headers.Add("X-Probe", "original");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("probe=original", body);
    }

    [Fact]
    public async Task Rewrite_ExpandsEnvironmentVariablesInUrlAndHeader()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pathVar = $"DEVPROXY_TEST_PATH_{suffix}";
        var headerVar = $"DEVPROXY_TEST_HEADER_{suffix}";
        Environment.SetEnvironmentVariable(pathVar, "/head$1");
        Environment.SetEnvironmentVariable(headerVar, "from-env");
        try
        {
            await using var origin = await FakeOrigin.StartAsync();
            var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
            var config = PluginConfig.FromJson($$"""
                {
                  "rewrites": [
                    { "in": { "url": "/get(ers)$" }, "out": { "url": "${env:{{pathVar}}}", "headers": [ { "name": "X-Probe", "value": "v-${env:{{headerVar}}}" } ] } }
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
            using var client = proxy.CreateHttpClient();

            using var response = await client.GetAsync(new Uri($"http://{origin.Host}/geters"));
            var body = await response.Content.ReadAsStringAsync();

            // /head + capture group "ers" referenced from the env var value.
            Assert.Contains(origin.ReceivedRequests, r => r.PathAndQuery == "/headers");
            Assert.Equal("probe=v-from-env", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable(pathVar, null);
            Environment.SetEnvironmentVariable(headerVar, null);
        }
    }

    [Fact]
    public async Task Rewrite_SkipsRule_WhenEnvironmentVariableMissing()
    {
        var missingVar = $"DEVPROXY_TEST_MISSING_{Guid.NewGuid():N}";
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "rewrites": [
                { "in": { "url": "/headers$" }, "out": { "url": "/status/503", "headers": [ { "name": "X-Probe", "value": "${env:{{missingVar}}}" } ] } }
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
        using var client = proxy.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{origin.Host}/headers"));
        request.Headers.Add("X-Probe", "original");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("probe=original", body);
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
        // string bodies must not be JSON-serialized (no surrounding quotes)
        Assert.Equal("mocked-by-test", body);
    }

    [Theory]
    [InlineData("/get")]
    [InlineData("/ge*")]
    public async Task MockResponse_Nth_CountsInterceptedRequestsWithoutFallbackMock(string mockPath)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}{{mockPath}}", "method": "GET", "nth": 2 },
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
        var uri = new Uri($"http://{origin.Host}/get");

        using var first = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Contains("mocked-by-test", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var third = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ignored-on-get")]
    public async Task MockResponse_Nth_WithFallbackMock_AppliesOnNthRequest(string? bodyFragment)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET", "bodyFragment": {{System.Text.Json.JsonSerializer.Serialize(bodyFragment)}}, "nth": 2 },
                  "response": { "statusCode": 201, "body": "nth-mock" }
                },
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 202, "body": "fallback-mock" }
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
        var uri = new Uri($"http://{origin.Host}/get");

        using var first = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var third = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("alpha", "beta")]
    public async Task MockResponse_Nth_DistinctMatchersHaveIndependentCounts(
        string? firstBody, string? secondBody)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var firstMethod = firstBody is null ? "GET" : "POST";
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/echo", "method": "{{firstMethod}}", "bodyFragment": {{System.Text.Json.JsonSerializer.Serialize(firstBody)}}, "nth": 1 },
                  "response": { "statusCode": 201 }
                },
                {
                  "request": { "url": "http://{{origin.Host}}/echo", "method": "POST", "bodyFragment": {{System.Text.Json.JsonSerializer.Serialize(secondBody)}}, "nth": 2 },
                  "response": { "statusCode": 202 }
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/echo");

        using var first = await client.PostAsync(uri, new StringContent(secondBody ?? ""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var request = new HttpRequestMessage(new HttpMethod(firstMethod), uri);
        if (firstBody is not null)
        {
            request.Content = new StringContent(firstBody);
        }
        using var second = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var third = await client.PostAsync(uri, new StringContent(secondBody ?? ""));
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("100")]
    [InlineData("-5")]
    [InlineData("150")]
    public async Task MockResponse_Rate_AlwaysAppliesWhenAbsentFullOrInvalid(string? rate)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 201 },
                  "rate": {{rate ?? "null"}}
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/get");

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task MockResponse_Rate0_PassesThroughToOrigin()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 503 },
                  "rate": 0
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/get");

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task MockResponse_Rate0_FallsThroughToNextMatchingMock()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 503 },
                  "rate": 0
                },
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 202 }
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/get");

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [Fact]
    public async Task MockResponse_Rate0_WithNth_IsNeverApplied()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET", "nth": 2 },
                  "response": { "statusCode": 201 },
                  "rate": 0
                },
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 202 }
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/get");

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [Fact]
    public async Task MockResponse_PartialRate_AppliesSomeOfTheTime()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 503 },
                  "rate": 50
                },
                {
                  "request": { "url": "http://{{origin.Host}}/get", "method": "GET" },
                  "response": { "statusCode": 202 }
                }
              ]
            }
            """);
        using var plugin = new MockResponsePlugin(
            SharedHttpClient, NullLogger<MockResponsePlugin>.Instance, urls, ProxyConfig, config);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/get");

        var statusCodes = new HashSet<HttpStatusCode>();
        // the chance of 100 rolls at 50% landing on the same side is ~2^-99
        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync(uri);
            _ = statusCodes.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.ServiceUnavailable], statusCodes.Order());
    }

    [Theory]
    [InlineData("/Get")]
    [InlineData("/Ge*")]
    public async Task MockResponse_MatchesUrlCaseInsensitively(string mockPath)
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "url": "http://{{origin.Host}}{{mockPath}}", "method": "GET" },
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

        // urlsToWatch matching ignores case, so mock URL matching must too.
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

    private static GraphRandomErrorPlugin CreateGraphRandomErrorPlugin(string configJson)
    {
        var plugin = new GraphRandomErrorPlugin(
            SharedHttpClient,
            NullLogger<GraphRandomErrorPlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch("graph.microsoft.com"),
            ProxyConfig,
            PluginConfig.FromJson(configJson));
        var command = new RootCommand();
        foreach (var option in plugin.GetOptions())
        {
            command.Options.Add(option);
        }
        plugin.OptionsLoaded(new OptionsLoadedArgs(command.Parse([])));
        return plugin;
    }

    [Fact]
    public async Task GraphRandomError_NoAllowedErrorsForMethod_PassesThrough()
    {
        // 507 isn't a built-in error for GET, so GET has no errors left to
        // throw while POST does.
        var plugin = CreateGraphRandomErrorPlugin("""{ "rate": 100, "allowedErrors": [507] }""");

        var get = TestExchange.Request("GET", "https://graph.microsoft.com/v1.0/me");
        await plugin.BeforeRequestAsync(get.RequestArgs, CancellationToken.None);
        Assert.False(get.State.HasBeenSet);

        var post = TestExchange.Request("POST", "https://graph.microsoft.com/v1.0/me/messages", body: "{}");
        await plugin.BeforeRequestAsync(post.RequestArgs, CancellationToken.None);
        Assert.True(post.State.HasBeenSet);
    }

    [Fact]
    public async Task GraphRandomError_BatchWithMethodWithoutAllowedErrors_PassesThrough()
    {
        var plugin = CreateGraphRandomErrorPlugin("""{ "rate": 100, "allowedErrors": [507] }""");

        var batch = TestExchange.Request(
            "POST",
            "https://graph.microsoft.com/v1.0/$batch",
            body: """
                {
                  "requests": [
                    { "id": "1", "method": "GET", "url": "/me" },
                    { "id": "2", "method": "POST", "url": "/me/messages", "body": {}, "headers": { "content-type": "application/json" } }
                  ]
                }
                """);
        await plugin.BeforeRequestAsync(batch.RequestArgs, CancellationToken.None);
        Assert.False(batch.State.HasBeenSet);
    }
}
