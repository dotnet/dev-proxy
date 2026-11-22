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
    [InlineData(-1, 8897, "port")]
    [InlineData(70000, 8897, "port")]
    [InlineData(8000, -1, "apiPort")]
    [InlineData(8000, 70000, "apiPort")]
    public void GetInvalidConfigPortMessage_OutOfRange_NamesSettingAndRange(int port, int apiPort, string setting)
    {
        var message = DevProxyCommand.GetInvalidConfigPortMessage(port, apiPort);

        Assert.NotNull(message);
        Assert.Contains($"'{setting}'", message, StringComparison.Ordinal);
        Assert.Contains("between 0 and 65535", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8000, 8897)]
    [InlineData(65535, 65535)]
    public void GetInvalidConfigPortMessage_InRange_ReturnsNull(int port, int apiPort)
    {
        Assert.Null(DevProxyCommand.GetInvalidConfigPortMessage(port, apiPort));
    }
}
