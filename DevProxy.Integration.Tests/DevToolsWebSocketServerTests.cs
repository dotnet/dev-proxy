// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.WebSockets;
using DevProxy.Plugins.Inspection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Covers the DevToolsPlugin WebSocket server's access checks (path token
/// and Origin) and how it maps the proxy IP address to the listener host.
/// </summary>
public sealed class DevToolsWebSocketServerTests
{
    private const string Token = "4f2b8f0e-7c1d-4b8e-9d3a-2a6f5e1c0b9d";

    [Theory]
    [InlineData("devtools://devtools")]
    [InlineData("http://localhost:9222")]
    [InlineData("http://127.0.0.1:9222")]
    public async Task AcceptsConnection_WithTokenAndDevToolsOrigin(string origin)
    {
        using var server = StartServer(out var port);

        using var client = await ConnectAsync(port, Token, origin);

        Assert.Equal(WebSocketState.Open, client.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData(Token + "/extra")]
    public async Task RejectsConnection_WithInvalidToken(string path)
    {
        using var server = StartServer(out var port);

        _ = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(port, path, "devtools://devtools"));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task RejectsConnection_FromOtherOrigins(string? origin)
    {
        using var server = StartServer(out var port);

        _ = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(port, Token, origin));
    }

    [Theory]
    [InlineData(null, "127.0.0.1", "127.0.0.1")]
    [InlineData("", "127.0.0.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "*", "127.0.0.1")]
    [InlineData("::", "*", "127.0.0.1")]
    [InlineData("192.168.1.10", "192.168.1.10", "192.168.1.10")]
    [InlineData("::1", "[::1]", "[::1]")]
    public void GetWebSocketHosts_MapsProxyIPAddress(string? ipAddress, string expectedListenerHost, string expectedConnectHost)
    {
        var (listenerHost, connectHost) = DevToolsPlugin.GetWebSocketHosts(ipAddress);

        Assert.Equal(expectedListenerHost, listenerHost);
        Assert.Equal(expectedConnectHost, connectHost);
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("https://evil\r\n[WS] forged", "https://evil\\u000d\\u000a[WS] forged")]
    public void SanitizeForLog_EscapesControlCharacters(string? value, string expected)
    {
        Assert.Equal(expected, WebSocketServer.SanitizeForLog(value));
    }

    private static WebSocketServer StartServer(out int port)
    {
        port = NetUtil.GetFreePort();
        var server = new WebSocketServer("127.0.0.1", port, Token, NullLogger.Instance);
        server.Start();
        return server;
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string path, string? origin)
    {
        var client = new ClientWebSocket();
        if (origin is not null)
        {
            client.Options.SetRequestHeader("Origin", origin);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{path}"), cts.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
