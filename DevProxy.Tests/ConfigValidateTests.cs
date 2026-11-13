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
              "ValidateSchemas": "False",
              "mySection": { "$schema": "{{SectionSchemaUrl}}", "rate": "high" }
            }
            """);

        Assert.DoesNotContain("mySection", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task ResolvesConfigSectionCaseInsensitivelyWithColonPath()
    {
        var result = await ValidateAsync($$"""
            {
              "plugins": [
                { "Name": "Plugin", "Enabled": true, "pluginPath": "plugin.dll", "ConfigSection": "Parent:MySection", "UrlsToWatch": ["https://api.example.com/*"] }
              ],
              "parent": { "mysection": { "$Schema": "{{SectionSchemaUrl}}", "rate": "high" } }
            }
            """);

        Assert.DoesNotContain("plugins[0].configSection", GetPaths(result, "warnings"));
        Assert.DoesNotContain("plugins[0].urlsToWatch", GetPaths(result, "warnings"));
        Assert.Contains("Parent:MySection", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task ResolvesConfigSectionWithArrayIndex()
    {
        var result = await ValidateAsync($$"""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "profiles:1:settings" }
              ],
              "urlsToWatch": ["https://api.example.com/*"],
              "profiles": [{}, { "settings": { "$schema": "{{SectionSchemaUrl}}", "rate": "high" } }]
            }
            """);

        Assert.DoesNotContain("plugins[0].configSection", GetPaths(result, "warnings"));
        Assert.Contains("profiles:1:settings", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task ReportsNonStringConfigSectionSchema()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "mySection" }
              ],
              "urlsToWatch": ["https://api.example.com/*"],
              "mySection": { "$schema": 42 }
            }
            """);

        Assert.Contains("mySection.$schema", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task ReportsNonObjectRoot()
    {
        var (exitCode, result) = await ValidateWithExitCodeAsync("[]");

        Assert.Equal(2, exitCode);
        Assert.Contains("configFile", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task ReportsNonStringSchema()
    {
        var (exitCode, result) = await ValidateWithExitCodeAsync("""
            {
              "$schema": 42,
              "plugins": [{ "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll" }],
              "urlsToWatch": ["https://api.example.com/*"]
            }
            """);

        Assert.Equal(2, exitCode);
        Assert.Contains("$schema", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task TreatsNullSchemaAsAbsent()
    {
        var result = await ValidateAsync("""
            {
              "$schema": null,
              "plugins": [{ "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll" }],
              "urlsToWatch": ["https://api.example.com/*"]
            }
            """);

        Assert.DoesNotContain("$schema", GetPaths(result, "errors"));
        Assert.Contains("$schema", GetPaths(result, "warnings"));
    }

    [Fact]
    public async Task TreatsWhitespaceConfigSectionSchemaAsAbsent()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [
                { "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll", "configSection": "mySection" }
              ],
              "urlsToWatch": ["https://api.example.com/*"],
              "mySection": { "$schema": "  " }
            }
            """);

        Assert.DoesNotContain(GetPaths(result, "errors"), p => p.StartsWith("mySection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsDuplicateConfigurationKeys()
    {
        var result = await ValidateAsync("""
            {
              "plugins": [{ "name": "Plugin", "enabled": true, "pluginPath": "plugin.dll" }],
              "urlsToWatch": ["https://a.example.com/*"],
              "UrlsToWatch": ["https://b.example.com/*"]
            }
            """);

        Assert.Contains("configFile", GetPaths(result, "errors"));
    }

    [Fact]
    public async Task AcceptsCaseInsensitiveKeyCollisionsInYaml()
    {
        var result = await ValidateAsync("""
            plugins:
              - name: Plugin
                enabled: true
                pluginPath: plugin.dll
            urlsToWatch:
              - https://api.example.com/*
            rate: 1
            Rate: 2
            """, "devproxyrc.yaml");

        Assert.DoesNotContain("configFile", GetPaths(result, "errors"));
    }

    [Theory]
    [InlineData(new[] { "validate" }, true)]
    [InlineData(new[] { "config", "validate" }, true)]
    [InlineData(new[] { "--output", "json", "validate" }, true)]
    [InlineData(new[] { "--output=json", "config", "validate" }, true)]
    [InlineData(new[] { "validate", "--help" }, false)]
    [InlineData(new[] { "config", "get", "validate" }, false)]
    [InlineData(new[] { "--config-file", "validate" }, false)]
    public void DetectsValidateInvocation(string[] args, bool expected) =>
        Assert.Equal(expected, DevProxyCommand.IsValidateInvocation(args));

    [Theory]
    [InlineData(new[] { "validate", "--config-file=my.json" }, "my.json")]
    [InlineData(new[] { "--output", "json", "config", "validate", "-c", "my.json" }, "my.json")]
    public void ParsesValidateArgs(string[] args, string expectedConfigFile)
    {
        var parseResult = ConfigCommand.ParseValidateArgs(args, out var configFileOptions, out _);

        Assert.Empty(parseResult.Errors);
        Assert.Contains(expectedConfigFile, configFileOptions.Select(o => parseResult.GetValue(o)));
    }

    [Fact]
    public void ReportsParseErrorForMissingConfigFileValue()
    {
        var parseResult = ConfigCommand.ParseValidateArgs(["validate", "-c"], out _, out _);

        Assert.NotEmpty(parseResult.Errors);
    }

    private async Task<JsonElement> ValidateAsync(string config, string fileName = "devproxyrc.json") =>
        (await ValidateWithExitCodeAsync(config, fileName)).Result;

    private async Task<(int ExitCode, JsonElement Result)> ValidateWithExitCodeAsync(string config, string fileName = "devproxyrc.json")
    {
        var configFile = Path.Combine(_tempDir, fileName);
        await File.WriteAllTextAsync(configFile, config);

        var logger = new CapturingLogger();
        using var httpClient = new HttpClient(new SchemaHandler());
        var exitCode = await ConfigCommand.ValidateConfigCoreAsync(configFile, true, httpClient, logger, CancellationToken.None);

        var json = Assert.Single(logger.Messages);
        using var doc = JsonDocument.Parse(json);
        return (exitCode, doc.RootElement.Clone());
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
