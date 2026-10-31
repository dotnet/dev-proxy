// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace DevProxy.Integration.Tests;

public sealed class MinimalPermissionsUtilsTests : IDisposable
{
    private const string Variable = "DEVPROXY_TEST_SPEC_TENANT";

    public MinimalPermissionsUtilsTests()
    {
        Environment.SetEnvironmentVariable(Variable, "a \"quoted\" value");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
    }

    [Fact]
    public void ResolveApiSpecVariables_Yaml_ResolvesValuesOnly()
    {
        var yaml = $$"""
        info:
          title: "${{{Variable}}}"
        ${{{Variable}}}: key
        """;
        var logger = new WarningLogger();

        var result = MinimalPermissionsUtils.ResolveApiSpecVariables(yaml, "spec.yaml", [], logger);

        var stream = new YamlStream();
        stream.Load(new StringReader(result));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal("a \"quoted\" value", ((YamlMappingNode)root["info"])["title"].ToString());
        Assert.Equal("key", root["${" + Variable + "}"].ToString());
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("spec.json", """{ "a": "$${TENANT_ID}", "b": "{TENANT_ID}" }""")]
    [InlineData("spec.yaml", "a: $${TENANT_ID}\nb: '{TENANT_ID}'")]
    public void ResolveApiSpecVariables_LegacyVariablesDontChangeEscapedPlaceholders(string fileName, string contents)
    {
        var logger = new WarningLogger();

        var result = MinimalPermissionsUtils.ResolveApiSpecVariables(contents, fileName, new() { ["TENANT_ID"] = "contoso" }, logger);

        var (a, b) = fileName.EndsWith(".json", StringComparison.Ordinal) ? ReadJson(result) : ReadYaml(result);
        Assert.Equal("${TENANT_ID}", a);
        Assert.Equal("contoso", b);
        _ = Assert.Single(logger.Warnings, w => w.Contains("deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveApiSpecVariables_Yaml_ResolvesUnquotedLegacyVariables()
    {
        var yaml = """
        servers:
          - url: {BASE_URL}
        escaped: $${BASE_URL}
        """;
        var logger = new WarningLogger();

        var result = MinimalPermissionsUtils.ResolveApiSpecVariables(yaml, "spec.yaml", new() { ["BASE_URL"] = "https://api.contoso.com" }, logger);

        var stream = new YamlStream();
        stream.Load(new StringReader(result));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var server = (YamlMappingNode)((YamlSequenceNode)root["servers"])[0];
        Assert.Equal("https://api.contoso.com", Assert.IsType<YamlScalarNode>(server["url"]).Value);
        Assert.Equal("${BASE_URL}", root["escaped"].ToString());
        _ = Assert.Single(logger.Warnings, w => w.Contains("deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveApiSpecVariables_Json_ResolvesLegacyVariablesInKeys()
    {
        var json = """{ "paths": { "/{TENANT_ID}/users/{id}": {}, "/$${TENANT_ID}": {} } }""";
        var logger = new WarningLogger();

        var result = MinimalPermissionsUtils.ResolveApiSpecVariables(json, "spec.json", new() { ["TENANT_ID"] = "contoso" }, logger);

        using var doc = JsonDocument.Parse(result);
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/contoso/users/{id}", out _));
        // keys aren't resolved, but the legacy pass must not change placeholders
        Assert.True(paths.TryGetProperty("/$${TENANT_ID}", out _));
        _ = Assert.Single(logger.Warnings, w => w.Contains("deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveApiSpecVariables_EscapedPlaceholderOnly_NoDeprecationWarning()
    {
        var logger = new WarningLogger();

        var result = MinimalPermissionsUtils.ResolveApiSpecVariables("""{ "a": "$${TENANT_ID}" }""", "spec.json", new() { ["TENANT_ID"] = "contoso" }, logger);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("${TENANT_ID}", doc.RootElement.GetProperty("a").GetString());
        Assert.Empty(logger.Warnings);
    }

    private static (string? A, string? B) ReadJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("a").GetString(), doc.RootElement.GetProperty("b").GetString());
    }

    private static (string? A, string? B) ReadYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (root["a"].ToString(), root["b"].ToString());
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}