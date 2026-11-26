// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

using Xunit;

namespace DevProxy.Tests;

public class PluginCommandTests
{
    private const string PluginPath = "./DevProxy.Tests.dll";

    private static List<PluginInfo> GetTestPlugins() =>
        [.. PluginCommand.GetPlugins(typeof(PluginCommandTests).Assembly, PluginPath)
            .Where(p => p.Name.StartsWith("PluginCommandTest", StringComparison.Ordinal))];

    [Fact]
    public void GetPlugins_ReturnsOnlyPublicNonAbstractNonGenericPlugins()
    {
        var plugins = GetTestPlugins();

        Assert.Equal(["PluginCommandTestConfigurablePlugin", "PluginCommandTestSimplePlugin"], plugins.Select(p => p.Name));
        Assert.All(plugins, p => Assert.Equal(PluginPath, p.PluginPath));
    }

    [Fact]
    public void GetPlugins_DetectsConfigurablePlugins()
    {
        var plugins = GetTestPlugins();

        Assert.True(plugins.Single(p => p.Name == "PluginCommandTestConfigurablePlugin").IsConfigurable);
        Assert.False(plugins.Single(p => p.Name == "PluginCommandTestSimplePlugin").IsConfigurable);
    }

    [Fact]
    public void ListPlugins_MissingAssembly_ReturnsErrorWithoutOutput()
    {
        var logger = new RecordingLogger();

        var exitCode = PluginCommand.ListPlugins(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.dll"), OutputFormat.Json, logger);

        Assert.Equal(1, exitCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains("Could not load plugin assembly", entry.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStandaloneAsync_MissingAssembly_ReturnsFailureWithoutHostServices()
    {
        var exitCode = await PluginCommand.RunStandaloneAsync([
            "plugin", "list",
            "--plugin-path", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.dll"),
            "--log-level", "None"
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void WritePlugins_JsonOutput_UsesStructuredResultEnvelope()
    {
        var logger = new RecordingLogger();
        using var output = new StringWriter();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", PluginPath, OutputFormat.Json, logger);

        var entry = Assert.Single(logger.Entries);
        var formatter = new JsonConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions()));
        formatter.Write(entry, null, output);

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("result", json.RootElement.GetProperty("type").GetString());
        var plugin = json.RootElement.GetProperty("data").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "PluginCommandTestConfigurablePlugin");
        Assert.Equal(PluginPath, plugin.GetProperty("pluginPath").GetString());
        Assert.True(plugin.GetProperty("isConfigurable").GetBoolean());
    }

    [Fact]
    public void WritePlugins_TextOutput_ListsPluginsAndUsageHint()
    {
        var logger = new RecordingLogger();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", PluginPath, OutputFormat.Text, logger);

        var text = logger.Text;
        Assert.Contains("  PluginCommandTestConfigurablePlugin (configurable)", text, StringComparison.Ordinal);
        Assert.Contains("  PluginCommandTestSimplePlugin" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains($"\"pluginPath\": \"{PluginPath}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WritePlugins_TextOutput_EscapesPluginPathInUsageHint()
    {
        var logger = new RecordingLogger();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", @"C:\plugins\MyPlugins.dll", OutputFormat.Text, logger);

        Assert.Contains(@"""pluginPath"": ""C:\\plugins\\MyPlugins.dll""", logger.Text, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry<string>> Entries { get; } = [];
        public string Text => string.Join(Environment.NewLine, Entries.Select(entry => entry.State));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry<string>(logLevel, nameof(PluginCommand), eventId, formatter(state, exception), exception, (message, _) => message));
    }
}

public sealed class PluginCommandTestSimplePlugin(ILogger logger, ISet<UrlToWatch> urlsToWatch) :
    BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(PluginCommandTestSimplePlugin);
}

public sealed class PluginCommandTestConfiguration
{
    public int Value { get; set; }
}

public sealed class PluginCommandTestConfigurablePlugin(
    HttpClient httpClient,
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection configurationSection) :
    BasePlugin<PluginCommandTestConfiguration>(httpClient, logger, urlsToWatch, proxyConfiguration, configurationSection)
{
    public override string Name => nameof(PluginCommandTestConfigurablePlugin);
}

public abstract class PluginCommandTestAbstractPlugin(ILogger logger, ISet<UrlToWatch> urlsToWatch) :
    BasePlugin(logger, urlsToWatch)
{
}

internal sealed class PluginCommandTestInternalPlugin(ILogger logger, ISet<UrlToWatch> urlsToWatch) :
    BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(PluginCommandTestInternalPlugin);
}

public sealed class PluginCommandTestGenericPlugin<T>(ILogger logger, ISet<UrlToWatch> urlsToWatch) :
    BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(PluginCommandTestGenericPlugin<T>);
}
