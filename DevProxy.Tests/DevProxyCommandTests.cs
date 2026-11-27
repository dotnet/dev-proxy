// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;

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
    [InlineData(true, "--log-level", "debug", "stdio", "npx")]
    [InlineData(false, "jwt", "create", "--name", "stdio", "--output", "json")]
    [InlineData(true, "--env", "A=1", "B=2", "stdio", "node", "server.js")]
    [InlineData(true, "-e", "A=1", "B=2", "--log-level", "debug", "stdio", "node")]
    [InlineData(true, "--urls-to-watch", "https://a/*", "https://b/*", "stdio", "node")]
    [InlineData(true, "--watch-pids", "1", "2", "stdio", "node")]
    [InlineData(true, "--watch-process-names", "a", "b", "stdio", "node")]
    [InlineData(false, "config", "get", "stdio")]
    [InlineData(false)]
    public void IsStdioSubcommand_DetectsSubcommandNotArgumentValues(bool expected, params string[] args) =>
        Assert.Equal(expected, DevProxyCommand.IsStdioSubcommand(args));
}
