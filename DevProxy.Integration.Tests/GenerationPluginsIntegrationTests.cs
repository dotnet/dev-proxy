// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for generation plugins, which turn the recorded <c>InterceptedResponse</c> log
/// stream into an artifact (HAR, .http, OpenAPI/TypeSpec spec, mock file) on
/// <c>AfterRecordingStopAsync</c>. These plugins write their output to the process current
/// working directory, so each test redirects the CWD to a temp folder (assembly test
/// parallelization is disabled — see TestParallelization.cs) and asserts both the stored
/// report and the generated file.
/// </summary>
public sealed class GenerationPluginsIntegrationTests
{
    private static ISet<UrlToWatch> Watch => KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com");

    private static RecordingArgs Recording(IEnumerable<RequestLog> logs) =>
        new(logs)
        {
            GlobalData = new() { [ProxyUtils.ReportsKey] = new Dictionary<string, object>() },
        };

    private static RequestLog SampleExchange() =>
        TestExchange
            .Request("GET", "https://api.contoso.com/users", headers: [("Accept", "application/json")])
            .WithResponse(
                HttpStatusCode.OK,
                headers: [("Content-Type", "application/json")],
                body: """[ { "id": 1, "name": "alpha" } ]""")
            .AsRequestLog(MessageType.InterceptedResponse);

    /// <summary>
    /// Runs <paramref name="action"/> with the process CWD redirected to a fresh temp
    /// directory, returning the directory so the caller can inspect generated files.
    /// </summary>
    private static async Task<DirectoryInfo> InTempCwdAsync(Func<Task> action)
    {
        var dir = Directory.CreateTempSubdirectory("devproxy-gen-");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            await action();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        return dir;
    }

    [Fact]
    public async Task HarGenerator_WritesHarFile()
    {
        var plugin = new HarGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HarGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var args = Recording([SampleExchange()]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var har = dir.GetFiles("devproxy-*.har").Single();
            var content = await File.ReadAllTextAsync(har.FullName);
            Assert.Contains("api.contoso.com/users", content, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MockGenerator_WritesMockFile()
    {
        var plugin = new MockGeneratorPlugin(NullLogger<MockGeneratorPlugin>.Instance, Watch);

        var args = Recording([SampleExchange()]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var mock = dir.GetFiles("mocks-*.json").Single();
            var content = await File.ReadAllTextAsync(mock.FullName);
            Assert.Contains("api.contoso.com/users", content, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HttpFileGenerator_WritesHttpFile()
    {
        var plugin = new HttpFileGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HttpFileGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var args = Recording([SampleExchange()]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var report = Assert.IsType<HttpFileGeneratorPluginReport>(
                ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
            Assert.NotEmpty(report);

            var httpFile = dir.GetFiles("requests_*.http").Single();
            var content = await File.ReadAllTextAsync(httpFile.FullName);
            Assert.Contains("api.contoso.com/users", content, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OpenApiSpecGenerator_WritesSpec()
    {
        var plugin = new OpenApiSpecGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<OpenApiSpecGeneratorPlugin>.Instance,
            Watch,
            new DisabledLanguageModelClient(),
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var args = Recording([SampleExchange()]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var report = Assert.IsType<OpenApiSpecGeneratorPluginReport>(
                ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
            Assert.NotEmpty(report);
            Assert.NotEmpty(dir.GetFiles("*.json"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TypeSpecGenerator_WritesSpec()
    {
        var plugin = new TypeSpecGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<TypeSpecGeneratorPlugin>.Instance,
            Watch,
            new DisabledLanguageModelClient(),
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var args = Recording([SampleExchange()]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var report = Assert.IsType<TypeSpecGeneratorPluginReport>(
                ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
            Assert.NotEmpty(report);
            Assert.NotEmpty(dir.GetFiles("*.tsp"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
