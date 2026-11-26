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
}

public class DevProxyCommandStartInvocationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(true, "--port", "0", "--as-system-proxy", "false")]
    [InlineData(true, "--output", "json", "--log-level", "debug")]
    [InlineData(true, "--output=json", "-u", "https://a.example/*", "https://b.example/*")]
    [InlineData(true, "--mocks-file", "mocks.json", "--no-mocks")]
    [InlineData(true, "--record", "--watch-pids", "1", "2")]
    [InlineData(false, "status")]
    [InlineData(false, "--output", "json", "status", "--pid", "1")]
    [InlineData(false, "--config-file", "devproxyrc.json", "--output", "json", "status")]
    [InlineData(false, "--log-level=debug", "system-proxy", "reset")]
    [InlineData(false, "--record", "stop")]
    [InlineData(false, "mocks", "from-http-responses")]
    [InlineData(false, "--help")]
    [InlineData(false, "--port", "0", "-h")]
    [InlineData(false, "--version")]
    [InlineData(false, "@missing-response-file.rsp")]
    public void IsStartInvocation_UsesParsedCommand(bool expected, params string[] args) =>
        Assert.Equal(expected, DevProxyCommand.IsStartInvocation(args));

    [Theory]
    [InlineData(true, "--port 0\n--output json")]
    [InlineData(false, "--output json\nstatus")]
    [InlineData(false, "--port 0\n--help")]
    public void IsStartInvocation_ExpandsResponseFiles(bool expected, string contents)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, contents);
            Assert.Equal(expected, DevProxyCommand.IsStartInvocation([$"@{path}"]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
