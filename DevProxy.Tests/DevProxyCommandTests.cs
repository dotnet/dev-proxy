// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using System.CommandLine;

using Xunit;

namespace DevProxy.Tests;

public class DevProxyCommandTests
{
    [Fact]
    public void ApplyDiscoverOption_True_EnablesRecording()
    {
        var config = new FakeProxyConfiguration { Record = false };

        DevProxyCommand.ApplyDiscoverOption(config, true);

        Assert.True(config.Record);
    }

    [Fact]
    public void ApplyDiscoverOption_False_DoesNotEnableRecording()
    {
        var config = new FakeProxyConfiguration { Record = false };

        DevProxyCommand.ApplyDiscoverOption(config, false);

        Assert.False(config.Record);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyDiscoverOption_NotSpecified_LeavesRecordingUnchanged(bool record)
    {
        var config = new FakeProxyConfiguration { Record = record };

        DevProxyCommand.ApplyDiscoverOption(config, null);

        Assert.Equal(record, config.Record);
    }

    [Fact]
    public void ApplyDiscoverOption_False_KeepsRecordingEnabledFromOtherSources()
    {
        var config = new FakeProxyConfiguration { Record = true };

        DevProxyCommand.ApplyDiscoverOption(config, false);

        Assert.True(config.Record);
    }

    [Theory]
    [InlineData(true, "stdio", "npx", "@devproxy/mcp")]
    [InlineData(true, "stdio", "-c", "myconfig.json", "node", "app.js")]
    [InlineData(false, "--log-level", "debug", "stdio", "npx")]
    [InlineData(false, "jwt", "create", "--name", "stdio", "--output", "json")]
    [InlineData(false, "config", "get", "stdio")]
    [InlineData(false)]
    public void IsStdioSubcommand_DetectsSubcommandOnlyAsFirstArgument(bool expected, params string[] args) =>
        Assert.Equal(expected, DevProxyCommand.IsStdioSubcommand(args));

    [Theory]
    [InlineData(null)]
    [InlineData(null, "--port", "0")]
    [InlineData(null, "--help")]
    [InlineData(null, "api", "show", "--log-level", "debug")]
    [InlineData("api show", "--log-level", "debug", "api", "show")]
    [InlineData("api show", "--port", "1", "api", "show")]
    [InlineData("api", "--output", "json", "api")]
    public void GetLeadingOptionsError_RejectsOptionsBeforeSubcommand(string? expectedCommandPath, params string[] args)
    {
        var logLevelOption = new Option<string>(DevProxyCommand.LogLevelOptionName) { Recursive = true };
        var outputOption = new Option<string>(DevProxyCommand.OutputOptionName) { Recursive = true };
        var portOption = new Option<int?>(DevProxyCommand.PortOptionName);
        var apiCommand = new Command("api") { new Command("show") };
        var rootCommand = new RootCommand { logLevelOption, outputOption, portOption, apiCommand };

        var error = DevProxyCommand.GetLeadingOptionsError(rootCommand.Parse(args), args);

        if (expectedCommandPath is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains($"devproxy {expectedCommandPath} [options]", error, StringComparison.Ordinal);
        }
    }
}
