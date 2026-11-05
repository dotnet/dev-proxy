// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;

using Xunit;

namespace DevProxy.Tests;

public class SystemProxyManagerTests
{
    [Fact]
    public void CreateToggleScriptStartInfo_PreservesPathAndArgumentsContainingSpaces()
    {
        var startInfo = SystemProxyManager.CreateToggleScriptStartInfo(
            "/Applications/Dev Proxy/toggle-proxy.sh",
            ["on", "proxy host", "8897"]);

        Assert.Equal("/bin/bash", startInfo.FileName);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(
            ["/Applications/Dev Proxy/toggle-proxy.sh", "on", "proxy host", "8897"],
            startInfo.ArgumentList);
    }
}

public class MacOSNetworkSetupTests
{
    [Fact]
    public void ParseNetworkServices_SkipsHeaderBlankAndDisabledServices()
    {
        var output = "An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Thunderbolt Bridge\nUSB 10/100/1000 LAN\n\n";

        var services = MacOSNetworkSetup.ParseNetworkServices(output);

        Assert.Equal(["Wi-Fi", "USB 10/100/1000 LAN"], services);
    }

    [Fact]
    public void ParseProxyEndpoint_ReadsEnabledServerAndPort()
    {
        var output = "Enabled: Yes\nServer: proxy.contoso.com\nPort: 3128\nAuthenticated Proxy Enabled: 0\n";

        var endpoint = MacOSNetworkSetup.ParseProxyEndpoint(output);

        Assert.True(endpoint.Enabled);
        Assert.Equal("proxy.contoso.com", endpoint.Server);
        Assert.Equal(3128, endpoint.Port);
    }

    [Fact]
    public void ParseProxyEndpoint_EmptyServer()
    {
        var endpoint = MacOSNetworkSetup.ParseProxyEndpoint("Enabled: No\nServer: \nPort: 0\n");

        Assert.False(endpoint.Enabled);
        Assert.Equal(string.Empty, endpoint.Server);
        Assert.Equal(0, endpoint.Port);
    }

    [Fact]
    public void GetRestoreCommands_RestoresServerAndTurnsDisabledEndpointsOff()
    {
        var settings = new MacOSServiceProxySettings
        {
            Service = "Wi-Fi",
            Web = new() { Enabled = true, Server = "proxy.contoso.com", Port = 3128 },
            SecureWeb = new() { Enabled = false, Server = "secure.contoso.com", Port = 3129 },
        };

        var commands = MacOSNetworkSetup.GetRestoreCommands(settings);

        Assert.Equal(3, commands.Count);
        Assert.Equal(["-setwebproxy", "Wi-Fi", "proxy.contoso.com", "3128"], commands[0]);
        Assert.Equal(["-setsecurewebproxy", "Wi-Fi", "secure.contoso.com", "3129"], commands[1]);
        Assert.Equal(["-setsecurewebproxystate", "Wi-Fi", "off"], commands[2]);
    }

    [Fact]
    public void GetRestoreCommands_NoPreviousServer_ReturnsNoCommands()
    {
        var settings = new MacOSServiceProxySettings
        {
            Service = "Wi-Fi",
            Web = new() { Enabled = false, Server = string.Empty, Port = 0 },
            SecureWeb = null,
        };

        Assert.Empty(MacOSNetworkSetup.GetRestoreCommands(settings));
    }
}

public class SystemProxySnapshotStoreTests
{
    [Fact]
    public void SaveLoadDelete_RoundTripsSnapshot()
    {
        var folder = Directory.CreateTempSubdirectory("devproxy-snapshot-");
        try
        {
            var path = Path.Combine(folder.FullName, "system-proxy-backup.json");
            var snapshot = new SystemProxySnapshot
            {
                Pid = 1234,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Windows = new() { ProxyEnable = 1, ProxyServer = "corp:8080" },
                MacOS = [new() { Service = "Wi-Fi", Web = new() { Enabled = true, Server = "corp", Port = 8080 } }],
            };

            SystemProxySnapshotStore.Save(path, snapshot);
            var loaded = SystemProxySnapshotStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(1234, loaded.Pid);
            Assert.Equal(1, loaded.Windows?.ProxyEnable);
            Assert.Equal("corp:8080", loaded.Windows?.ProxyServer);
            var service = Assert.Single(loaded.MacOS!);
            Assert.Equal("Wi-Fi", service.Service);
            Assert.Equal(8080, service.Web?.Port);

            SystemProxySnapshotStore.Delete(path);
            Assert.Null(SystemProxySnapshotStore.Load(path));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(SystemProxySnapshotStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SystemProxyResetDetectionTests
{
    [Theory]
    [InlineData("system-proxy", "reset")]
    [InlineData("system-proxy", "reset", "--force")]
    [InlineData("--output=json", "system-proxy", "reset")]
    [InlineData("system-proxy", "--no-color", "reset")]
    [InlineData("--log-level", "debug", "system-proxy", "reset", "--bogus")]
    public void IsResetInvocation_ReturnsTrue(params string[] args) =>
        Assert.True(DevProxy.Commands.SystemProxyCommand.IsResetInvocation(args));

    [Theory]
    [InlineData("system-proxy")]
    [InlineData("reset")]
    [InlineData("stop")]
    [InlineData]
    public void IsResetInvocation_ReturnsFalse(params string[] args) =>
        Assert.False(DevProxy.Commands.SystemProxyCommand.IsResetInvocation(args));
}
