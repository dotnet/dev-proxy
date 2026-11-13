// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    public void GetPlugins_ReturnsOnlyPublicNonAbstractPlugins()
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
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = PluginCommand.ListPlugins(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.dll"), OutputFormat.Json, output, error);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Could not load plugin assembly", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WritePlugins_JsonOutput_IsParsableArray()
    {
        using var output = new StringWriter();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", PluginPath, OutputFormat.Json, output);

        using var json = JsonDocument.Parse(output.ToString());
        var plugin = json.RootElement.EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "PluginCommandTestConfigurablePlugin");
        Assert.Equal(PluginPath, plugin.GetProperty("pluginPath").GetString());
        Assert.True(plugin.GetProperty("isConfigurable").GetBoolean());
    }

    [Fact]
    public void WritePlugins_TextOutput_ListsPluginsAndUsageHint()
    {
        using var output = new StringWriter();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", PluginPath, OutputFormat.Text, output);

        var text = output.ToString();
        Assert.Contains("  PluginCommandTestConfigurablePlugin (configurable)", text, StringComparison.Ordinal);
        Assert.Contains("  PluginCommandTestSimplePlugin" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains($"\"pluginPath\": \"{PluginPath}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WritePlugins_TextOutput_EscapesPluginPathInUsageHint()
    {
        using var output = new StringWriter();

        PluginCommand.WritePlugins(GetTestPlugins(), "/full/DevProxy.Tests.dll", @"C:\plugins\MyPlugins.dll", OutputFormat.Text, output);

        Assert.Contains(@"""pluginPath"": ""C:\\plugins\\MyPlugins.dll""", output.ToString(), StringComparison.Ordinal);
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
