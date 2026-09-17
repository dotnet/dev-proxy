// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Plugins.Guidance;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for guidance plugins, which emit advisory log entries (Warning/Tip/Failed)
/// rather than altering the HTTP exchange. Each plugin is constructed with a logger backed
/// by <see cref="CapturingLoggerFactory"/>, driven with a <see cref="TestExchange"/> shaped
/// to satisfy its trigger, and asserted on the captured <c>RequestLog</c>.
///
/// Most of these gate on the upstream being <c>graph.microsoft.com</c>, which the loopback
/// origin cannot impersonate through real routing — so they are driven at the plugin hook
/// with the engine's real canonical session (see <see cref="TestExchange"/>).
///
/// NOT hermetic (deferred): <c>GraphSelectGuidancePlugin</c> requires a populated Microsoft
/// Graph metadata SQLite database (MSGraphDb) built from Graph OpenAPI definitions.
/// </summary>
public sealed class GuidancePluginsIntegrationTests
{
    private static ISet<UrlToWatch> GraphWatch => KestrelProxyHarness.BuildUrlsToWatch("graph.microsoft.com");

    private static Logger<T> Logger<T>(CapturingLoggerFactory factory) => new(factory);

    [Fact]
    public async Task GraphBetaSupport_WarnsOnBetaRequest()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new GraphBetaSupportGuidancePlugin(Logger<GraphBetaSupportGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/beta/me")
            .WithResponse(HttpStatusCode.OK);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.Contains(factory.LogsOfType(MessageType.Warning), l => l.Message.Contains("beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GraphClientRequestId_WarnsWhenHeaderMissing()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new GraphClientRequestIdGuidancePlugin(Logger<GraphClientRequestIdGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange.Request("GET", "https://graph.microsoft.com/v1.0/me");
        await plugin.BeforeRequestAsync(exchange.RequestArgs, CancellationToken.None);

        Assert.Contains(factory.LogsOfType(MessageType.Warning), l => l.Message.Contains("client-request-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GraphSdk_TipsOnErrorWithoutSdk()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new GraphSdkGuidancePlugin(Logger<GraphSdkGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange
            .Request("GET", "https://graph.microsoft.com/v1.0/me")
            .WithResponse(HttpStatusCode.NotFound);
        await plugin.AfterResponseAsync(exchange.ResponseArgs, CancellationToken.None);

        Assert.NotEmpty(factory.LogsOfType(MessageType.Tip));
    }

    [Fact]
    public async Task ODataPaging_WarnsOnManualSkipToken()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new ODataPagingGuidancePlugin(Logger<ODataPagingGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange.Request("GET", "https://graph.microsoft.com/v1.0/users?$skiptoken=abc123");
        await plugin.BeforeRequestAsync(exchange.RequestArgs, CancellationToken.None);

        Assert.Contains(factory.LogsOfType(MessageType.Warning), l => l.Message.Contains("paging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ODSPSearch_WarnsOnDeprecatedDriveSearch()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new ODSPSearchGuidancePlugin(Logger<ODSPSearchGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange.Request("GET", "https://graph.microsoft.com/v1.0/me/drive/root/search(q='report')");
        await plugin.BeforeRequestAsync(exchange.RequestArgs, CancellationToken.None);

        Assert.Contains(factory.LogsOfType(MessageType.Warning), l => l.Message.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GraphConnector_FailsOnPatchWithoutSchema()
    {
        var factory = new CapturingLoggerFactory();
        var plugin = new GraphConnectorGuidancePlugin(Logger<GraphConnectorGuidancePlugin>(factory), GraphWatch);

        var exchange = TestExchange.Request("PATCH", "https://graph.microsoft.com/v1.0/external/connections/test/schema", body: "");
        await plugin.BeforeRequestAsync(exchange.RequestArgs, CancellationToken.None);

        Assert.NotEmpty(factory.LogsOfType(MessageType.Failed));
    }

    [Fact]
    public async Task CachingGuidance_WarnsOnRepeatRequestWithinWindow()
    {
        var factory = new CapturingLoggerFactory();
        var config = PluginConfig.FromJson("""{ "cacheThresholdSeconds": 30 }""");
        var plugin = new CachingGuidancePlugin(
            TestDefaults.HttpClient,
            Logger<CachingGuidancePlugin>(factory),
            GraphWatch,
            new TestProxyConfiguration(),
            config);

        var url = "https://graph.microsoft.com/v1.0/me";
        await plugin.BeforeRequestAsync(TestExchange.Request("GET", url).RequestArgs, CancellationToken.None);
        await plugin.BeforeRequestAsync(TestExchange.Request("GET", url).RequestArgs, CancellationToken.None);

        Assert.Contains(factory.LogsOfType(MessageType.Warning), l => l.Message.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }
}
