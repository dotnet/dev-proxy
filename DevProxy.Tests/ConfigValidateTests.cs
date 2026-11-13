// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DevProxy.Tests;

public sealed class ConfigValidateTests : IDisposable
{
    private const string SectionSchemaUrl = "https://example.com/schemas/v0.0.0/plugin.schema.json";
    private const string SectionSchema = """
        {
          "type": "object",
          "properties": {
            "rate": { "type": "integer" }
          }
        }
        """;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("devproxy-validate-tests").FullName;

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public async Task ReportsPluginWithoutUrlsToWatch()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [
                { "name": "WithUrls", "enabled": true, "pluginPath": "plugin.dll", "urlsToWatch": ["https://api.example.com/*"] },
                { "name": "WithoutUrls", "enabled": true, "pluginPath": "plugin.dll" },
                { "name": "Disabled", "enabled": false, "pluginPath": "plugin.dll" }
              ]
            }
            """);

        var warningPaths = GetPaths(result, "warnings");
        Assert.DoesNotContain("plugins[0].urlsToWatch", warningPaths);
        Assert.Contains("plugins[1].urlsToWatch", warningPaths);
        Assert.DoesNotContain("plugins[2].urlsToWatch", warningPaths);
    }

    [Fact]
    public async Task DoesNotReportPluginUrlsWhenGlobalUrlsConfigured()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll" }
              ],
              "urlsToWatch": ["https://api.example.com/*"]
            }
            """);

        Assert.DoesNotContain("plugins[0].urlsToWatch", GetPaths(result, "warnings"));
    }

    [Fact]
    public async Task ReportsMissingConfigSection()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "missing" }
              ],
              "urlsToWatch": ["https://api.example.com/*"]
            }
            """);

        Assert.Contains("plugins[0].configSection", GetPaths(result, "warnings"));
    }

    [Fact]
    public async Task ReportsInvalidPluginConfigSection()
    {
        var result = await ValidateAsync($$"""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "mySection" }
              ],
              "urlsToWatch": ["https://api.example.com/*"],
              "mySection": { "$schema": "{{SectionSchemaUrl}}", "rate": "high" }
            }
            """);

        Assert.Contains("mySection", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task SkipsPluginConfigSectionValidationWhenSchemaValidationDisabled()
    {
        var result = await ValidateAsync($$"""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "mySection" }
              ],
              "urlsToWatch": ["https://api.example.com/*"],
              "validateSchemas": false,
              "mySection": { "$schema": "{{SectionSchemaUrl}}", "rate": "high" }
            }
            """);

        Assert.DoesNotContain("mySection", GetPaths(result, "errors"));
    }

    private async Task<JsonElement> ValidateAsync(string config)
    {
        var configFile = Path.Combine(_tempDir, "devproxyrc.json");
        await File.WriteAllTextAsync(configFile, config);

        var logger = new CapturingLogger();
        using var httpClient = new HttpClient(new SchemaHandler());
        _ = await ConfigCommand.ValidateConfigCoreAsync(configFile, true, httpClient, logger, CancellationToken.None);

        var json = Assert.Single(logger.Messages);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static List<string> GetPaths(JsonElement result, string property) =>
        [.. result.GetProperty(property).EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];

    private sealed class SchemaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.ToString() == SectionSchemaUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SectionSchema, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
