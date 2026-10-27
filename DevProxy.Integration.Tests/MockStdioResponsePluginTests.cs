// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

public sealed class MockStdioResponsePluginTests
{
    [Fact]
    public async Task Nth_RegexAndFragmentWithIdenticalText_HaveIndependentCounts()
    {
        var config = PluginConfig.FromJson("""
            {
              "mocks": [
                {
                  "request": { "bodyRegex": "^ping$", "nth": 1 },
                  "response": { "stdout": "regex" }
                },
                {
                  "request": { "bodyFragment": "^ping$", "nth": 2 },
                  "response": { "stdout": "fragment" }
                }
              ]
            }
            """);
        using var httpClient = new HttpClient();
        using var plugin = new MockStdioResponsePlugin(
            httpClient, NullLogger<MockStdioResponsePlugin>.Instance, new HashSet<UrlToWatch>(),
            new TestProxyConfiguration(), config);

        var first = CreateRequest("^ping$");
        await plugin.BeforeStdinAsync(first, CancellationToken.None);
        Assert.False(first.ResponseState.HasBeenSet);
        Assert.Null(first.StdoutResponse);

        var second = CreateRequest("ping");
        await plugin.BeforeStdinAsync(second, CancellationToken.None);
        Assert.True(second.ResponseState.HasBeenSet);
        Assert.Equal("regex", second.StdoutResponse);

        var third = CreateRequest("^ping$");
        await plugin.BeforeStdinAsync(third, CancellationToken.None);
        Assert.True(third.ResponseState.HasBeenSet);
        Assert.Equal("fragment", third.StdoutResponse);
    }

    [Theory]
    [InlineData(null, "ping")]
    [InlineData("", "ping")]
    [InlineData("ping", "ignored")]
    public async Task Nth_EffectiveMatcher_SharesFallbackCount(string? bodyRegex, string bodyFragment)
    {
        var fallbackRegex = string.IsNullOrEmpty(bodyRegex) ? null : bodyRegex;
        var config = PluginConfig.FromJson($$"""
            {
              "mocks": [
                {
                  "request": { "bodyRegex": {{System.Text.Json.JsonSerializer.Serialize(bodyRegex)}}, "bodyFragment": {{System.Text.Json.JsonSerializer.Serialize(bodyFragment)}}, "nth": 2 },
                  "response": { "stdout": "nth" }
                },
                {
                  "request": { "bodyRegex": {{System.Text.Json.JsonSerializer.Serialize(fallbackRegex)}}, "bodyFragment": "ping" },
                  "response": { "stdout": "fallback" }
                }
              ]
            }
            """);
        using var httpClient = new HttpClient();
        using var plugin = new MockStdioResponsePlugin(
            httpClient, NullLogger<MockStdioResponsePlugin>.Instance, new HashSet<UrlToWatch>(),
            new TestProxyConfiguration(), config);

        foreach (var expected in new[] { "fallback", "nth", "fallback" })
        {
            var request = CreateRequest("ping");
            await plugin.BeforeStdinAsync(request, CancellationToken.None);
            Assert.True(request.ResponseState.HasBeenSet);
            Assert.Equal(expected, request.StdoutResponse);
        }
    }

    private static StdioRequestArgs CreateRequest(string body) => new()
    {
        Body = Encoding.UTF8.GetBytes(body),
        ResponseState = new(),
        Session = new() { Command = "test", Args = [] }
    };
}
