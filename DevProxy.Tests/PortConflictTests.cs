// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Proxy;
using DevProxy.State;
using Microsoft.AspNetCore.Connections;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DevProxy.Tests;

public class PortConflictTests
{
    [Fact]
    public void IsPortAvailable_ReturnsFalse_WhenPortIsInUse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortConflict.IsPortAvailable("127.0.0.1", port));
    }

    [Fact]
    public void IsPortAvailable_ReturnsTrue_WhenPortIsFree()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Assert.True(PortConflict.IsPortAvailable("127.0.0.1", port));
    }

    [Theory]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    [InlineData("not-an-ip", 8000)]
    public void IsPortAvailable_LeavesUncheckableInputToKestrel(string ipAddress, int port)
    {
        Assert.True(PortConflict.IsPortAvailable(ipAddress, port));
    }

    private static ProxyInstanceState State(int pid, string proxyUrl, int port, string apiUrl) =>
        new() { Pid = pid, ProxyUrl = proxyUrl, Port = port, ApiUrl = apiUrl };

    [Fact]
    public void FindOwningInstance_MatchesProxyPort()
    {
        var states = new[]
        {
            State(1, "http://127.0.0.1:8001", 8001, "http://127.0.0.1:8898/"),
            State(2, "http://127.0.0.1:8000", 8000, "http://127.0.0.1:8897/")
        };

        Assert.Equal(2, PortConflict.FindOwningInstance(states, "127.0.0.1", 8000, excludePid: 0)?.Pid);
    }

    [Fact]
    public void FindOwningInstance_MatchesApiPort()
    {
        var states = new[]
        {
            State(1, "http://127.0.0.1:8001", 8001, "http://127.0.0.1:8898/"),
            State(2, "http://127.0.0.1:8000", 8000, "http://127.0.0.1:8897")
        };

        Assert.Equal(2, PortConflict.FindOwningInstance(states, "127.0.0.1", 8897, excludePid: 0)?.Pid);
    }

    [Fact]
    public void FindOwningInstance_MatchesAcrossPortKinds()
    {
        // Another instance's API listener can hold the port requested for the proxy, and vice versa.
        var states = new[] { State(2, "http://127.0.0.1:8000", 8000, "http://127.0.0.1:8897") };

        Assert.Equal(2, PortConflict.FindOwningInstance(states, "127.0.0.1", 8897, excludePid: 0)?.Pid);
        Assert.Equal(2, PortConflict.FindOwningInstance(states, null, 8000, excludePid: 0)?.Pid);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000", "127.0.0.1", true)]
    [InlineData("http://0.0.0.0:8000", "127.0.0.1", true)]
    [InlineData("http://127.0.0.1:8000", "0.0.0.0", true)]
    [InlineData("http://[::]:8000", "::1", true)]
    [InlineData("http://[::1]:8000", "[::1]", true)]
    [InlineData("http://127.0.0.1:8000", "192.168.1.10", false)]
    [InlineData("http://[::1]:8000", "127.0.0.1", false)]
    [InlineData("http://localhost:8000", "127.0.0.1", false)]
    [InlineData("http://127.0.0.1:8000", "not-an-ip", false)]
    [InlineData("http://0.0.0.0:8000", "[::1]", false)]
    [InlineData("http://[::1]:8000", "0.0.0.0", false)]
    [InlineData("http://[::]:8000", "127.0.0.1", true)]
    [InlineData("http://127.0.0.1:8000", "::", true)]
    public void FindOwningInstance_RequiresOverlappingAddress(string proxyUrl, string requestedAddress, bool expectedMatch)
    {
        var states = new[] { State(2, proxyUrl, 8000, "http://127.0.0.1:8897") };

        var owner = PortConflict.FindOwningInstance(states, requestedAddress, 8000, excludePid: 0);

        Assert.Equal(expectedMatch, owner is not null);
    }

    [Fact]
    public void FindOwningInstance_IgnoresCurrentProcessAndPortZero()
    {
        var states = new[] { State(5, "http://127.0.0.1:8000", 8000, "http://127.0.0.1:0") };

        Assert.Null(PortConflict.FindOwningInstance(states, "127.0.0.1", 8000, excludePid: 5));
        Assert.Null(PortConflict.FindOwningInstance(states, "127.0.0.1", 0, excludePid: 0));
    }

    [Fact]
    public void FormatMessage_DevProxyInstance_SuggestsStopAndPortZero()
    {
        var message = PortConflict.FormatMessage(PortKind.Api, 8897, new ProxyInstanceState { Pid = 42 });

        Assert.Contains("API port 8897 is already in use by another Dev Proxy instance (PID: 42).", message, StringComparison.Ordinal);
        Assert.Contains("devproxy stop --pid 42", message, StringComparison.Ordinal);
        Assert.Contains("devproxy --api-port 0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessage_OtherApplication_SuggestsPortZeroAndConfig()
    {
        var message = PortConflict.FormatMessage(PortKind.Proxy, 8000, owner: null);

        Assert.Contains("Port 8000 is already in use by another application.", message, StringComparison.Ordinal);
        Assert.Contains("devproxy --port 0", message, StringComparison.Ordinal);
        Assert.Contains("'port' in your devproxyrc.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("devproxy stop", message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAddressInUse_DetectsWrappedKestrelException()
    {
        var ex = new IOException("Failed to bind", new AddressInUseException("Address already in use"));

        Assert.True(PortConflict.IsAddressInUse(ex));
        Assert.False(PortConflict.IsAddressInUse(new IOException("other")));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("8000", true)]
    [InlineData("65535", true)]
    [InlineData("65536", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    public void PortOptions_ValidateRange(string value, bool valid)
    {
        var option = new Option<int?>(DevProxyCommand.PortOptionName);
        option.Validators.Add(DevProxyCommand.ValidatePort);
        var command = new RootCommand { option };

        var result = command.Parse([DevProxyCommand.PortOptionName, value]);

        Assert.Equal(valid, result.Errors.Count == 0);
    }
}
