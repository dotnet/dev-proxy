// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Tests;

public sealed class BasePluginSchemaValidationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-plugin").FullName;

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

    private sealed class TestPlugin(HttpClient httpClient, ILogger logger, IProxyConfiguration proxyConfiguration, IConfigurationSection configurationSection) :
        BasePlugin<TestPluginConfiguration>(httpClient, logger, new HashSet<UrlToWatch>(), proxyConfiguration, configurationSection)
    {
        public override string Name => nameof(TestPlugin);
    }

    private sealed class TestPluginConfiguration;
}
