// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Mocking;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Covers serving mocks loaded from a mocks file (JSON bodies), where the plugin
/// reuses each mock's serialized body across requests instead of re-cloning and
/// re-serializing it per request.
/// </summary>
public sealed class MockResponseServingTests
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TestProxyConfiguration ProxyConfig = new();

    [Fact]
    public async Task JsonBody_IsServedIdenticallyOnEveryRequest_AndMatchesClonedSerialization()
    {
        await using var origin = await FakeOrigin.StartAsync();
        using var plugin = CreatePlugin(origin.Host, $$"""
            [
              {
                "request": { "url": "http://{{origin.Host}}/items/*" },
                "response": {
                  "statusCode": 200,
                  "headers": [ { "name": "x-mock", "value": "yes" } ],
                  "body": { "name": "Zoë <b>&amp;</b>", "price": 1.50, "tags": ["a", "b"], "nested": { "n": null } }
                }
              }
            ]
            """);
        var mock = plugin.Configuration.Mocks.Single();
        // What the plugin served before reusing serialized bodies: clone, then serialize.
        var expected = ProxyUtils.SerializeBody(((MockResponse)mock.Clone()).Response!.Body);

        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(new Uri($"http://{origin.Host}/items/{i}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("yes", response.Headers.GetValues("x-mock").Single());
            Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Placeholders_AreReplacedPerRequest_WithoutModifyingTheLoadedMock()
    {
        await using var origin = await FakeOrigin.StartAsync();
        using var plugin = CreatePlugin(origin.Host, $$"""
            [
              {
                "request": { "url": "http://{{origin.Host}}/greet", "method": "POST" },
                "response": { "body": { "greeting": "@request.body.name", "price": 1.50 } }
              }
            ]
            """);
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();
        var uri = new Uri($"http://{origin.Host}/greet");

        foreach (var name in new[] { "Ada", "Bob" })
        {
            using var content = new StringContent($$"""{ "name": "{{name}}" }""", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(uri, content);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(name, json.RootElement.GetProperty("greeting").GetString());
        }

        // Without a request body there's nothing to replace: the loaded mock is served
        // as authored, proving the earlier replacements didn't leak into it.
        using var noBody = await client.PostAsync(uri, null);
        using var raw = JsonDocument.Parse(await noBody.Content.ReadAsStringAsync());
        Assert.Equal("@request.body.name", raw.RootElement.GetProperty("greeting").GetString());
    }

    [Fact]
    public async Task FileBody_IsReadFromDiskOnEveryRequest()
    {
        var dir = Directory.CreateTempSubdirectory("devproxy-mocks-");
        try
        {
            var bodyFile = Path.Combine(dir.FullName, "body.json");
            await File.WriteAllTextAsync(bodyFile, """{ "version": 1 }""");

            await using var origin = await FakeOrigin.StartAsync();
            using var plugin = CreatePlugin(origin.Host, $$"""
                [
                  {
                    "request": { "url": "http://{{origin.Host}}/file" },
                    "response": { "body": "@body.json" }
                  }
                ]
                """);
            plugin.Configuration.MocksFile = Path.Combine(dir.FullName, "mocks.json");
            await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
            using var client = proxy.CreateHttpClient();
            var uri = new Uri($"http://{origin.Host}/file");

            Assert.Equal("""{ "version": 1 }""", await client.GetStringAsync(uri));

            await File.WriteAllTextAsync(bodyFile, """{ "version": 2 }""");
            Assert.Equal("""{ "version": 2 }""", await client.GetStringAsync(uri));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WildcardMocks_MatchTheRightMock_AmongManyPatterns()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var mocks = string.Join(",", Enumerable.Range(0, 50).Select(i => $$"""
            {
              "request": { "url": "http://{{origin.Host}}/api/{{i}}/*" },
              "response": { "body": { "mock": {{i}} } }
            }
            """));
        using var plugin = CreatePlugin(origin.Host, $"[{mocks}]");
        await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        foreach (var i in new[] { 49, 0, 17, 49 })
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(new Uri($"http://{origin.Host}/API/{i}/items?x=1")));
            Assert.Equal(i, json.RootElement.GetProperty("mock").GetInt32());
        }
    }

    [Theory]
    [InlineData("https://contoso.com/api/*", "https://CONTOSO.com/api/v1/items", true)]
    [InlineData("https://contoso.com/api/*", "https://contoso.com/other", false)]
    [InlineData("https://contoso.com/a.b", "https://contoso.com/axb", false)]
    [InlineData("https://*.contoso.com/*", "https://api.contoso.com/v1", true)]
    public void WildcardUrlPattern_MatchesLikePatternToRegex(string pattern, string url, bool expected) =>
        Assert.Equal(expected, WildcardUrlPattern.IsMatch(url, pattern));

    [Theory]
    [InlineData("wss://contoso.com/socket/*", "https://contoso.com/socket/1", true)]
    [InlineData("ws://contoso.com/socket/*", "http://contoso.com/socket/1", true)]
    [InlineData("wss://contoso.com/socket/*", "http://contoso.com/socket/1", false)]
    public void WildcardUrlPattern_NormalizesWebSocketScheme(string pattern, string url, bool expected) =>
        Assert.Equal(expected, WildcardUrlPattern.IsWebSocketMatch(url, pattern));

    private static MockResponsePlugin CreatePlugin(string originHost, string mocksJson)
    {
        var plugin = new MockResponsePlugin(
            SharedHttpClient,
            NullLogger<MockResponsePlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch(originHost),
            ProxyConfig,
            PluginConfig.Empty());
        // Deserialize the way MockResponsesLoader does, so bodies are JsonElements.
        plugin.Configuration.Mocks = JsonSerializer.Deserialize<MockResponseConfiguration>(
            $$"""{ "mocks": {{mocksJson}} }""", ProxyUtils.JsonSerializerOptions)!.Mocks;
        return plugin;
    }
}
