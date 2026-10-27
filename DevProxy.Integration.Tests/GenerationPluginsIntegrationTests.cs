// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Generation;
using Microsoft.Extensions.Logging;
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
    public async Task HarGenerator_RedactsCookiesAndSensitiveQueryParameters()
    {
        var plugin = new HarGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HarGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var entry = await GenerateSingleHarEntryAsync(plugin, CookieAndSecretsExchange());
        var request = entry.GetProperty("request");
        var response = entry.GetProperty("response");

        Assert.All(request.GetProperty("cookies").EnumerateArray(), c => Assert.Equal("REDACTED", c.GetProperty("value").GetString()));
        Assert.All(response.GetProperty("cookies").EnumerateArray(), c => Assert.Equal("REDACTED", c.GetProperty("value").GetString()));
        Assert.Equal("session", response.GetProperty("cookies")[0].GetProperty("name").GetString());

        var url = request.GetProperty("url").GetString()!;
        Assert.DoesNotContain("s3cr3t", url, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", url, StringComparison.Ordinal);
        Assert.Contains("access_token=REDACTED", url, StringComparison.Ordinal);
        Assert.Contains("sig=REDACTED", url, StringComparison.Ordinal);
        Assert.Contains("page=2", url, StringComparison.Ordinal);

        var query = request.GetProperty("queryString").EnumerateArray()
            .ToDictionary(q => q.GetProperty("name").GetString()!, q => q.GetProperty("value").GetString());
        Assert.Equal("REDACTED", query["access_token"]);
        Assert.Equal("REDACTED", query["sig"]);
        Assert.Equal("2", query["page"]);
    }

    [Theory]
    [InlineData("?page")]
    [InlineData("??page")]
    [InlineData("?access_token")]
    public async Task HarGenerator_PreservesParameterLeadingQuestionMarks(string parameterName)
    {
        var plugin = new HarGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HarGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var log = TestExchange
            .Request("GET", $"https://api.contoso.com/users?{parameterName}=2&access_token=secret")
            .WithResponse(HttpStatusCode.OK)
            .AsRequestLog(MessageType.InterceptedResponse);

        var entry = await GenerateSingleHarEntryAsync(plugin, log);
        var request = entry.GetProperty("request");

        Assert.Equal($"https://api.contoso.com/users?{parameterName}=2&access_token=REDACTED",
            request.GetProperty("url").GetString());
        var query = request.GetProperty("queryString").EnumerateArray()
            .ToDictionary(q => q.GetProperty("name").GetString()!, q => q.GetProperty("value").GetString());
        Assert.Equal("2", query[parameterName]);
        Assert.Equal("REDACTED", query["access_token"]);
    }

    [Fact]
    public async Task HarGenerator_KeepsCookiesAndQueryParametersWhenIncludingSensitiveInformation()
    {
        var plugin = new HarGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HarGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.FromJson("""{ "includeSensitiveInformation": true }"""));

        var entry = await GenerateSingleHarEntryAsync(plugin, CookieAndSecretsExchange());
        var request = entry.GetProperty("request");
        var response = entry.GetProperty("response");

        Assert.Equal("abc123", request.GetProperty("cookies")[0].GetProperty("value").GetString());
        Assert.Equal("xyz789", response.GetProperty("cookies")[0].GetProperty("value").GetString());
        Assert.Contains("access_token=s3cr3t", request.GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    private static RequestLog CookieAndSecretsExchange() =>
        TestExchange
            .Request(
                "GET",
                "https://api.contoso.com/users?access_token=s3cr3t&page=2&sig=s3cr3t",
                headers: [("Cookie", "sid=abc123; theme=dark")])
            .WithResponse(
                HttpStatusCode.OK,
                headers: [("Content-Type", "application/json"), ("Set-Cookie", "session=xyz789; Path=/; HttpOnly")],
                body: "[]")
            .AsRequestLog(MessageType.InterceptedResponse);

    private static async Task<JsonElement> GenerateSingleHarEntryAsync(HarGeneratorPlugin plugin, RequestLog log)
    {
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(Recording([log]), CancellationToken.None));
        try
        {
            var har = dir.GetFiles("devproxy-*.har").Single();
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(har.FullName));
            return doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().Single().Clone();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HarGenerator_WritesWebSocketMessages()
    {
        var plugin = new HarGeneratorPlugin(
            TestDefaults.HttpClient,
            NullLogger<HarGeneratorPlugin>.Instance,
            Watch,
            new TestProxyConfiguration(),
            PluginConfig.Empty());

        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500);
        var wsExchange = TestExchange.WebSocket(
            "https://api.contoso.com/socket",
            new WebSocketMessageRecord(WebSocketMessageDirection.Send, WebSocketMessageType.Text, Encoding.UTF8.GetBytes("ping"), t0),
            new WebSocketMessageRecord(WebSocketMessageDirection.Receive, WebSocketMessageType.Text, Encoding.UTF8.GetBytes("pong"), t0.AddMilliseconds(10)),
            new WebSocketMessageRecord(WebSocketMessageDirection.Send, WebSocketMessageType.Binary, new byte[] { 0x01, 0x02, 0x03 }, t0.AddMilliseconds(20)))
            .AsRequestLog(MessageType.InterceptedResponse);

        var args = Recording([wsExchange]);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var har = dir.GetFiles("devproxy-*.har").Single();
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(har.FullName));
            var entry = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().Single();

            Assert.Equal("websocket", entry.GetProperty("_resourceType").GetString());

            var messages = entry.GetProperty("_webSocketMessages").EnumerateArray().ToArray();
            Assert.Equal(3, messages.Length);

            // send text "ping": type=send, opcode=1 (RFC 6455 text), data is raw UTF-8.
            Assert.Equal("send", messages[0].GetProperty("type").GetString());
            Assert.Equal(1, messages[0].GetProperty("opcode").GetInt32());
            Assert.Equal("ping", messages[0].GetProperty("data").GetString());
            Assert.Equal(1_700_000_000.5, messages[0].GetProperty("time").GetDouble(), 3);

            // receive text "pong".
            Assert.Equal("receive", messages[1].GetProperty("type").GetString());
            Assert.Equal(1, messages[1].GetProperty("opcode").GetInt32());
            Assert.Equal("pong", messages[1].GetProperty("data").GetString());

            // send binary: opcode=2 (RFC 6455 binary), data is base64.
            Assert.Equal("send", messages[2].GetProperty("type").GetString());
            Assert.Equal(2, messages[2].GetProperty("opcode").GetInt32());
            Assert.Equal(Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 }), messages[2].GetProperty("data").GetString());
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
    public async Task MockGenerator_PreservesRecordedOrderForSameUrl()
    {
        var plugin = new MockGeneratorPlugin(NullLogger<MockGeneratorPlugin>.Instance, Watch);

        // enough entries to exceed the insertion-sort threshold of an unstable sort
        const int count = 40;
        var logs = Enumerable.Range(0, count)
            .Select(i => TestExchange
                .Request("GET", i % 2 == 0 ? "https://api.contoso.com/a" : "https://api.contoso.com/b")
                .WithResponse(
                    HttpStatusCode.OK,
                    headers: [("Content-Type", "application/json")],
                    body: $$"""{ "seq": {{i}} }""")
                .AsRequestLog(MessageType.InterceptedResponse))
            .ToList();

        var args = Recording(logs);
        var dir = await InTempCwdAsync(() => plugin.AfterRecordingStopAsync(args, CancellationToken.None));
        try
        {
            var mockFile = dir.GetFiles("mocks-*.json").Single();
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(mockFile.FullName));
            var mocks = doc.RootElement.GetProperty("mocks").EnumerateArray()
                .Select(m => (
                    Url: m.GetProperty("request").GetProperty("url").GetString(),
                    Seq: m.GetProperty("response").GetProperty("body").GetProperty("seq").GetInt32()))
                .ToArray();

            Assert.Equal(count, mocks.Length);

            var expected = Enumerable.Range(0, count).Where(i => i % 2 == 1)
                .Select(i => ("https://api.contoso.com/b", i))
                .Concat(Enumerable.Range(0, count).Where(i => i % 2 == 0)
                    .Select(i => ("https://api.contoso.com/a", i)));
            Assert.Equal(expected, mocks.Select(m => (m.Url!, m.Seq)));
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
        var logger = new MessageCapturingLogger<TypeSpecGeneratorPlugin>();
        var plugin = new TypeSpecGeneratorPlugin(
            TestDefaults.HttpClient,
            logger,
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
            Assert.Contains(logger.Messages, m => m.StartsWith("Writing TypeSpec file to ", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, m => m.StartsWith("Created TypeSpec file ", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("OpenAPI", StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class MessageCapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
