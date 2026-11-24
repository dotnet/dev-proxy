// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for reporting plugins, which observe the recorded <c>RequestLog</c> stream and
/// emit a structured report into <c>GlobalData[ProxyUtils.ReportsKey][PluginName]</c> on
/// <c>AfterRecordingStopAsync</c> — exactly as the host's ProxyStateController drives them at
/// stop-recording. Each test feeds engine-shaped logs (via <see cref="TestExchange.AsRequestLog"/>),
/// invokes the stop hook, and asserts the stored report.
///
/// NOT hermetic (deferred): GraphMinimalPermissions(+Guidance) call Microsoft Graph permission
/// endpoints; MinimalCsomPermissions needs the shipped CSOM types definition + CSOM bodies;
/// ApiCenter* require Azure API Center. Those are integration-with-live-backend (Bucket 3); only
/// their backend-independent logic (permission exclusion, report rendering) is covered here.
/// </summary>
public sealed class ReportingPluginsIntegrationTests
{
    private static RecordingArgs Recording(IEnumerable<RequestLog> logs) =>
        new(logs)
        {
            GlobalData = new() { [ProxyUtils.ReportsKey] = new Dictionary<string, object>() },
        };

    private static T GetReport<T>(RecordingArgs args, string pluginName) where T : class =>
        Assert.IsAssignableFrom<T>(((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[pluginName]);

    [Fact]
    public async Task UrlDiscovery_CollectsDistinctInterceptedUrls()
    {
        var watch = KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com");
        var plugin = new UrlDiscoveryPlugin(NullLogger<UrlDiscoveryPlugin>.Instance, watch);

        var logs = new[]
        {
            TestExchange.Request("GET", "https://api.contoso.com/users").AsRequestLog(),
            TestExchange.Request("GET", "https://api.contoso.com/orders").AsRequestLog(),
            TestExchange.Request("GET", "https://api.contoso.com/users").AsRequestLog(),
        };
        var args = Recording(logs);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = GetReport<UrlDiscoveryPluginReport>(args, plugin.Name);
        Assert.Equal(
            ["https://api.contoso.com/orders", "https://api.contoso.com/users"],
            report.Data);
    }

    [Fact]
    public async Task ExecutionSummary_GroupsInterceptedRequests()
    {
        var watch = KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com");
        var config = PluginConfig.FromJson("""{ "groupBy": "url" }""");
        var plugin = new ExecutionSummaryPlugin(
            TestDefaults.HttpClient,
            NullLogger<ExecutionSummaryPlugin>.Instance,
            watch,
            new TestProxyConfiguration(),
            config);

        var logs = new[]
        {
            TestExchange.Request("GET", "https://api.contoso.com/users").AsRequestLog(),
            TestExchange.Request("POST", "https://api.contoso.com/orders").AsRequestLog(),
        };
        var args = Recording(logs);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = GetReport<ExecutionSummaryPluginReportBase>(args, plugin.Name);
        Assert.NotEmpty(report.Data);
    }

    // Engine-shaped log stream for a single passed-through request, including the
    // proxy-internal lifecycle messages (timestamp, intercepted response, finished)
    // that must never surface as summary categories.
    private static RequestLog[] PassedThroughRequestLogs()
    {
        var exchange = TestExchange.Request("GET", "https://api.contoso.com/users");
        var context = new LoggingContext(exchange.Session);
        return
        [
            exchange.AsRequestLog(),
            new("2026-11-03T15:42:24.7350485+00:00", MessageType.Timestamp, context),
            new("URL not matched", MessageType.Skipped, context),
            new("Passed through", MessageType.PassedThrough, context),
            new("GET https://api.contoso.com/users", MessageType.InterceptedResponse, context),
            new("GET https://api.contoso.com/users", MessageType.FinishedProcessingRequest, context),
        ];
    }

    private static ExecutionSummaryPlugin CreateExecutionSummaryPlugin(string groupBy) =>
        new(
            TestDefaults.HttpClient,
            NullLogger<ExecutionSummaryPlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com"),
            new TestProxyConfiguration(),
            PluginConfig.FromJson($$"""{ "groupBy": "{{groupBy}}" }"""));

    [Fact]
    public async Task ExecutionSummary_ByUrl_ExcludesInternalMessageTypes()
    {
        var plugin = CreateExecutionSummaryPlugin("url");
        var args = Recording(PassedThroughRequestLogs());

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = GetReport<ExecutionSummaryPluginReportByUrl>(args, plugin.Name);
        var request = Assert.Single(report.Data);
        Assert.Equal("GET https://api.contoso.com/users", request.Key);
        var category = Assert.Single(request.Value);
        Assert.Equal("Requests passed through", category.Key);
        Assert.Equal(new Dictionary<string, int> { ["Passed through"] = 1 }, category.Value);

        var markdown = report.ToMarkdown();
        Assert.DoesNotContain("Unknown", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-03", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown", report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionSummary_ByUrl_HandlesLogWithoutPrecedingInterceptedRequest()
    {
        var plugin = CreateExecutionSummaryPlugin("url");
        var exchange = TestExchange.Request("GET", "https://api.contoso.com/users");
        var args = Recording([exchange.AsRequestLog(MessageType.PassedThrough)]);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = GetReport<ExecutionSummaryPluginReportByUrl>(args, plugin.Name);
        var request = Assert.Single(report.Data);
        Assert.Equal("GET https://api.contoso.com/users", request.Key);
        var category = Assert.Single(request.Value);
        Assert.Equal("Requests passed through", category.Key);
        Assert.Equal(new Dictionary<string, int> { ["GET https://api.contoso.com/users"] = 1 }, category.Value);
    }

    [Fact]
    public async Task ExecutionSummary_ByMessageType_ExcludesInternalMessageTypes()
    {
        var plugin = CreateExecutionSummaryPlugin("messageType");
        var args = Recording(PassedThroughRequestLogs());

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = GetReport<ExecutionSummaryPluginReportByMessageType>(args, plugin.Name);
        Assert.Equal(
            ["Requests intercepted", "Requests passed through"],
            report.Data.Keys.Order());
        Assert.Equal(
            new Dictionary<string, int> { ["GET https://api.contoso.com/users"] = 1 },
            report.Data["Requests passed through"]["Requests passed through"]);

        Assert.DoesNotContain("Unknown", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown", report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinimalPermissions_ReportsPermissionsFromApiSpec()
    {
        var dir = Directory.CreateTempSubdirectory("devproxy-minperms-");
        try
        {
            // OpenAPI spec with an OAuth2 scope on GET /users — the minimal permission the
            // recorded request requires.
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "contoso.json"), """
            {
              "openapi": "3.0.0",
              "info": { "title": "Contoso", "version": "1.0" },
              "servers": [ { "url": "https://api.contoso.com" } ],
              "components": {
                "securitySchemes": {
                  "oauth2": {
                    "type": "oauth2",
                    "flows": {
                      "authorizationCode": {
                        "authorizationUrl": "https://login.contoso.com/authorize",
                        "tokenUrl": "https://login.contoso.com/token",
                        "scopes": { "User.Read": "Read users", "User.Write": "Write users" }
                      }
                    }
                  }
                }
              },
              "paths": {
                "/users": {
                  "get": {
                    "security": [ { "oauth2": [ "User.Read" ] } ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """);

            var watch = KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com");
            var proxyConfig = new TestProxyConfiguration
            {
                ConfigFile = Path.Combine(dir.FullName, "devproxyrc.json"),
            };
            var config = PluginConfig.FromJson($$"""{ "apiSpecsFolderPath": "{{dir.FullName.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

            var plugin = new MinimalPermissionsPlugin(
                TestDefaults.HttpClient,
                NullLogger<MinimalPermissionsPlugin>.Instance,
                watch,
                proxyConfig,
                config);

            using var host = new PluginTestHost(proxyConfig);
            await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);

            var args = Recording([
                TestExchange.Request("GET", "https://api.contoso.com/users").AsRequestLog(),
            ]);

            await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

            var report = GetReport<MinimalPermissionsPluginReport>(args, plugin.Name);
            Assert.NotNull(report);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApiCenterMinimalPermissions_ExcludesOidcScopesFromExcessivePermissions()
    {
        var excessive = ApiCenterMinimalPermissionsPlugin.GetExcessivePermissions(
            tokenPermissions: ["User.Read", "User.Write", "openid", "profile", "offline_access", "email"],
            minimalPermissions: ["User.Read"],
            permissionsToExclude: ["profile", "openid", "offline_access", "email"]);

        Assert.Equal(["User.Write"], excessive);
    }

    [Fact]
    public void ApiCenterMinimalPermissions_WithoutExclusionsReportsAllExcessivePermissions()
    {
        var excessive = ApiCenterMinimalPermissionsPlugin.GetExcessivePermissions(
            tokenPermissions: ["User.Read", "openid"],
            minimalPermissions: ["User.Read"],
            permissionsToExclude: null);

        Assert.Equal(["openid"], excessive);
    }

    [Fact]
    public void ApiCenterMinimalPermissionsReport_ListsExcludedPermissions()
    {
        var report = new ApiCenterMinimalPermissionsPluginReport
        {
            Results = [],
            UnmatchedRequests = [],
            Errors = [],
            ExcludedPermissions = ["openid", "profile"],
        };

        Assert.Contains("- `openid`", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.Contains("openid, profile", report.ToPlainText(), StringComparison.Ordinal);
    }
}
