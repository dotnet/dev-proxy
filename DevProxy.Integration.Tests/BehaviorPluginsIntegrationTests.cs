// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Behavior;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Per-plugin integration coverage for the <c>Behavior/</c> plugins, proving each still
/// works end-to-end through the Kestrel engine (real request/response data reaching the
/// plugin's hooks). Behaviour is asserted purely from the HTTP response the client sees.
///
/// <code>
///   client ─▶ Kestrel engine ─▶ [behavior plugin] ─▶ FakeOrigin
///                                      │
///                       overrides status / adds latency / throttles
/// </code>
/// </summary>
public sealed class BehaviorPluginsIntegrationTests
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly TestProxyConfiguration ProxyConfig = new();

    [Fact]
    public async Task GenericRandomError_Rate100_OverridesOriginWithConfiguredError()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        // rate:100 ⇒ always fail; a single configured 503 error keyed by a wildcard URL.
        var config = PluginConfig.FromJson($$"""
            {
              "rate": 100,
              "errors": [
                {
                  "request": { "url": "http://{{origin.Host}}/*" },
                  "responses": [ { "statusCode": 503 } ]
                }
              ]
            }
            """);
        var plugin = new GenericRandomErrorPlugin(
            SharedHttpClient,
            NullLogger<GenericRandomErrorPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        // /get would return 200 "hello get" if the plugin did NOT override it.
        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Latency_AddsConfiguredDelayBeforeForwarding()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        const int minMs = 400;
        var config = PluginConfig.FromJson($$"""
            { "minMs": {{minMs}}, "maxMs": {{minMs + 50}} }
            """);
        var plugin = new LatencyPlugin(
            SharedHttpClient,
            NullLogger<LatencyPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Allow scheduling slack below the configured floor, but it must clearly exceed
        // an un-delayed localhost round-trip (single-digit ms).
        Assert.True(
            stopwatch.ElapsedMilliseconds >= minMs - 100,
            $"Expected >= {minMs - 100}ms of injected latency, saw {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task RateLimiting_ThrottlesOnceResourcesAreExhausted()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        // rateLimit:2 / costPerRequest:2 ⇒ request #1 drains to 0 (passes through),
        // request #2 goes negative and is throttled with 429.
        var config = PluginConfig.FromJson("""
            { "rateLimit": 2, "costPerRequest": 2, "resetTimeWindowSeconds": 300 }
            """);
        var plugin = new RateLimitingPlugin(
            SharedHttpClient,
            NullLogger<RateLimitingPlugin>.Instance,
            urls,
            ProxyConfig,
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [plugin]);
        using var client = proxy.CreateHttpClient();

        using var first = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        using var second = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task RateLimitingPlusRetryAfter_ThrottledResponseCarriesRetryAfter()
    {
        await using var origin = await FakeOrigin.StartAsync();
        var urls = KestrelProxyHarness.BuildUrlsToWatch(origin.Host);

        var rateLimiting = new RateLimitingPlugin(
            SharedHttpClient,
            NullLogger<RateLimitingPlugin>.Instance,
            urls,
            ProxyConfig,
            PluginConfig.FromJson("""
                { "rateLimit": 2, "costPerRequest": 2, "resetTimeWindowSeconds": 300 }
                """));
        var retryAfter = new RetryAfterPlugin(
            NullLogger<RetryAfterPlugin>.Instance,
            urls);

        await using var proxy = await KestrelProxyHarness.StartAsync(
            origin.Host, [rateLimiting, retryAfter]);
        using var client = proxy.CreateHttpClient();

        // #1 passes, #2 is throttled (429 + Retry-After) once resources are exhausted.
        using var first = await client.GetAsync(new Uri($"http://{origin.Host}/get"));
        using var throttled = await client.GetAsync(new Uri($"http://{origin.Host}/get"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(
            throttled.Headers.TryGetValues("Retry-After", out var values),
            "Throttled response should carry a Retry-After header.");
        Assert.True(
            int.TryParse(
                string.Join(string.Empty, values!),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _),
            "Retry-After header should be an integer seconds value.");
    }
}
