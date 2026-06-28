// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Integration coverage for the two mocking plugins that need <c>InitializeAsync</c> /
/// out-of-band behavior and therefore don't fit the request-intercept template:
///
///   • <see cref="MockRequestPlugin"/> — event-driven; on <c>MockRequestAsync</c> it
///     fires an outbound request via its injected <see cref="HttpClient"/>. Verified by
///     pointing it at <see cref="FakeOrigin"/> and asserting the origin was hit.
///   • <see cref="CrudApiPlugin"/> — serves an in-memory REST API from a data file,
///     short-circuiting matched requests (origin never contacted). Verified end-to-end
///     through the Kestrel engine against a temp api.json + data.json.
/// </summary>
public sealed class InitializingMockingPluginsIntegrationTests
{
    [Fact]
    public async Task MockRequestPlugin_FiresConfiguredOutboundRequest()
    {
        await using var origin = await FakeOrigin.StartAsync();

        var config = PluginConfig.FromJson($$"""
        {
          "request": {
            "url": "http://{{origin.Host}}/get",
            "method": "GET"
          }
        }
        """);

        var plugin = new MockRequestPlugin(
            TestDefaults.HttpClient,
            NullLogger<MockRequestPlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch(origin.Host),
            new TestProxyConfiguration(),
            config);

        await plugin.MockRequestAsync(EventArgs.Empty, CancellationToken.None);

        Assert.Contains(origin.ReceivedRequests, r => r.Method == "GET" && r.PathAndQuery == "/get");
    }

    [Fact]
    public async Task CrudApiPlugin_ServesInMemoryDataThroughEngine()
    {
        await using var origin = await FakeOrigin.StartAsync();

        var dir = Directory.CreateTempSubdirectory("devproxy-crud-");
        try
        {
            var baseUrl = $"http://{origin.Host}/api/items";
            await File.WriteAllTextAsync(
                Path.Combine(dir.FullName, "data.json"),
                """[ { "id": 1, "name": "alpha" }, { "id": 2, "name": "beta" } ]""");
            await File.WriteAllTextAsync(
                Path.Combine(dir.FullName, "api.json"),
                $$"""
                {
                  "baseUrl": "{{baseUrl}}",
                  "dataFile": "data.json",
                  "actions": [ { "action": "getAll", "url": "" } ]
                }
                """);

            var apiFile = Path.Combine(dir.FullName, "api.json");
            var proxyConfig = new TestProxyConfiguration
            {
                ConfigFile = Path.Combine(dir.FullName, "devproxyrc.json"),
            };
            var config = PluginConfig.FromJson($$"""{ "apiFile": "{{apiFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

            var plugin = new CrudApiPlugin(
                TestDefaults.HttpClient,
                NullLogger<CrudApiPlugin>.Instance,
                KestrelProxyHarness.BuildUrlsToWatch(origin.Host),
                proxyConfig,
                config);

            using var host = new PluginTestHost(proxyConfig);
            await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);

            await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
            using var client = proxy.CreateHttpClient();

            using var response = await client.GetAsync(new Uri(baseUrl));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("alpha", body, StringComparison.Ordinal);
            Assert.Contains("beta", body, StringComparison.Ordinal);
            // CrudApi short-circuits: the origin must never have received the request.
            Assert.DoesNotContain(origin.ReceivedRequests, r => r.PathAndQuery.StartsWith("/api/items", StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
