// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Tests;

public sealed class BasePluginSchemaValidationTests : IDisposable
{
    private const string Schema = """
        {
          "type": "object",
          "properties": { "name": { "type": "string", "maxLength": 5 } }
        }
        """;
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-plugin").FullName;
    // unique per test so that the per-process schema and validation caches don't leak between tests
    private readonly string _schemaUrl = $"https://schemas.example.test/plugin-{Guid.NewGuid()}.json";

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task InitializeAsync_SchemaCannotBeRetrieved_LogsWarningInsteadOfError()
    {
        var configFile = Path.Combine(_folder, "devproxyrc.json");
        await File.WriteAllTextAsync(configFile, """{ "testPlugin": { "$schema": "https://schemas.example.test/plugin.schema.json" } }""");
        var configurationSection = new ConfigurationBuilder().AddJsonFile(configFile).Build().GetSection("testPlugin");
        var proxyConfiguration = new FakeProxyConfiguration { ConfigFile = configFile, ValidateSchemas = true };
        var logger = new RecordingLogger();
        using var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);
        using var plugin = new TestPlugin(httpClient, logger, proxyConfiguration, configurationSection);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        await plugin.InitializeAsync(new() { ServiceProvider = serviceProvider }, CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.LogLevel == LogLevel.Warning && m.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.LogLevel >= LogLevel.Error);
    }

    [Fact]
    public async Task InitializeAsync_UnchangedSection_SkipsValidation()
    {
        var configFile = await WriteConfigAsync("""{ "testPlugin": { "$schema": "{schema}", "name": "abc" } }""");

        var first = await InitializeAsync(configFile, "testPlugin");
        var second = await InitializeAsync(configFile, "testPlugin");

        Assert.Equal(1, ValidationCount(first));
        Assert.Equal(0, ValidationCount(second));
        Assert.DoesNotContain(second.Messages, m => m.LogLevel >= LogLevel.Warning);
    }

    [Fact]
    public async Task InitializeAsync_SectionChangedToInvalid_ValidatesAgain()
    {
        var configFile = await WriteConfigAsync("""{ "testPlugin": { "$schema": "{schema}", "name": "abc" } }""");
        _ = await InitializeAsync(configFile, "testPlugin");

        _ = await WriteConfigAsync("""{ "testPlugin": { "$schema": "{schema}", "name": "toolong" } }""");
        var logger = await InitializeAsync(configFile, "testPlugin");

        Assert.Equal(1, ValidationCount(logger));
        Assert.Contains(logger.Messages, m => m.LogLevel == LogLevel.Error && m.Message.Contains("validation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_SectionsSharingSchema_AreCachedIndependently()
    {
        var configFile = await WriteConfigAsync("""
            {
              "validPlugin": { "$schema": "{schema}", "name": "abc" },
              "invalidPlugin": { "$schema": "{schema}", "name": "toolong" }
            }
            """);
        _ = await InitializeAsync(configFile, "validPlugin");

        var invalid = await InitializeAsync(configFile, "invalidPlugin");
        var valid = await InitializeAsync(configFile, "validPlugin");

        Assert.Equal(1, ValidationCount(invalid));
        Assert.Contains(invalid.Messages, m => m.LogLevel == LogLevel.Error && m.Message.Contains("validation failed", StringComparison.Ordinal));
        Assert.Equal(0, ValidationCount(valid));
    }

    private static int ValidationCount(RecordingLogger logger) =>
        logger.Messages.Count(m => m.Message.StartsWith("Validating JSON against schema", StringComparison.Ordinal));

    private async Task<string> WriteConfigAsync(string contents)
    {
        var configFile = Path.Combine(_folder, "devproxyrc.json");
        await File.WriteAllTextAsync(configFile, contents.Replace("{schema}", _schemaUrl, StringComparison.Ordinal));
        return configFile;
    }

    private static async Task<RecordingLogger> InitializeAsync(string configFile, string sectionName)
    {
        var configurationSection = new ConfigurationBuilder().AddJsonFile(configFile).Build().GetSection(sectionName);
        var proxyConfiguration = new FakeProxyConfiguration { ConfigFile = configFile, ValidateSchemas = true };
        var logger = new RecordingLogger();
        using var handler = new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Schema) });
        using var httpClient = new HttpClient(handler);
        using var plugin = new TestPlugin(httpClient, logger, proxyConfiguration, configurationSection);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        await plugin.InitializeAsync(new() { ServiceProvider = serviceProvider }, CancellationToken.None);
        return logger;
    }

    private sealed class TestPlugin(HttpClient httpClient, ILogger logger, IProxyConfiguration proxyConfiguration, IConfigurationSection configurationSection) :
        BasePlugin<TestPluginConfiguration>(httpClient, logger, new HashSet<UrlToWatch>(), proxyConfiguration, configurationSection)
    {
        public override string Name => nameof(TestPlugin);
    }

    private sealed class TestPluginConfiguration;
}
