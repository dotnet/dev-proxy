// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FrameworkWsMessageType = System.Net.WebSockets.WebSocketMessageType;

// The ClientWebSocket trusts the engine's MITM leaf (no OS trust store in tests), exactly
// like KestrelProxyHarness.CreateHttpClient does for HTTPS rows.
#pragma warning disable CA5359

namespace DevProxy.Integration.Tests;

/// <summary>
/// End-to-end coverage for <see cref="WebSocketMockResponsePlugin"/> through the real
/// <see cref="DevProxy.Proxy.Kestrel.KestrelProxyEngine"/>: a genuine
/// <see cref="ClientWebSocket"/> dials <c>wss://</c> THROUGH the proxy, which MITMs the
/// CONNECT (the plugin answers the handshake itself — no origin is ever dialed) and runs
/// the scripted conversation.
///
/// <code>
///   ClientWebSocket ──CONNECT ws.example.test:443──▶ Kestrel engine (MITM)
///                   ──GET Upgrade: websocket───────▶ WebSocketMockResponsePlugin
///                   ◀── 101 + scripted frames ──────  (proxy IS the WS server)
/// </code>
///
/// <para>
/// <c>wss://</c> (not <c>ws://</c>) is used deliberately: it forces CONNECT + TLS + MITM,
/// the exact path WebSocket mocking must work on. The fake host never resolves because
/// the proxy short-circuits the mock before any origin dial.
/// </para>
/// </summary>
public sealed class WebSocketMockIntegrationTests
{
    private const string Host = "ws.example.test";

    [Fact]
    public async Task MocksWebSocket_OnConnect_Reactive_AndClose_OverMitm()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var config = PluginConfig.FromJson($$"""
        {
          "mocks": [
            {
              "url": "wss://{{Host}}/socket",
              "onConnect": [ { "body": "welcome" } ],
              "rules": [
                { "match": { "bodyRegex": "^ping$" }, "responses": [ { "body": "pong" } ] },
                {
                  "match": { "bodyFragment": "bye" },
                  "responses": [ { "body": "goodbye" } ],
                  "closeAfter": true
                }
              ]
            }
          ]
        }
        """);

        var plugin = new WebSocketMockResponsePlugin(
            TestDefaults.HttpClient,
            NullLogger<WebSocketMockResponsePlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch(Host),
            new TestProxyConfiguration(),
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(Host, [plugin]);

        using var ws = new ClientWebSocket();
        ws.Options.Proxy = new WebProxy(
            $"http://127.0.0.1:{proxy.Port.ToString(CultureInfo.InvariantCulture)}");
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        await ws.ConnectAsync(new Uri($"wss://{Host}/socket"), cts.Token);

        // on-connect scripted message arrives immediately after the handshake.
        Assert.Equal("welcome", await ReceiveTextAsync(ws, cts.Token));

        // reactive rule: ping → pong.
        await SendTextAsync(ws, "ping", cts.Token);
        Assert.Equal("pong", await ReceiveTextAsync(ws, cts.Token));

        // contains-match rule replies then closes.
        await SendTextAsync(ws, "bye now", cts.Token);
        Assert.Equal("goodbye", await ReceiveTextAsync(ws, cts.Token));

        // closeAfter: the mock server sends a close frame.
        var close = await ws.ReceiveAsync(new byte[16], cts.Token);
        Assert.Equal(FrameworkWsMessageType.Close, close.MessageType);
    }

    [Fact]
    public async Task DoesNotMock_WhenNoMockMatchesUrl()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Mock is scoped to a DIFFERENT path; the request to /other must not be mocked.
        var config = PluginConfig.FromJson($$"""
        {
          "mocks": [
            { "url": "wss://{{Host}}/socket", "onConnect": [ { "body": "welcome" } ] }
          ]
        }
        """);

        var plugin = new WebSocketMockResponsePlugin(
            TestDefaults.HttpClient,
            NullLogger<WebSocketMockResponsePlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch(Host),
            new TestProxyConfiguration(),
            config);

        await using var proxy = await KestrelProxyHarness.StartAsync(Host, [plugin]);

        using var ws = new ClientWebSocket();
        ws.Options.Proxy = new WebProxy(
            $"http://127.0.0.1:{proxy.Port.ToString(CultureInfo.InvariantCulture)}");
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        // No mock matches /other and there is no origin to relay to, so the upgrade fails
        // rather than completing the scripted handshake.
        await Assert.ThrowsAnyAsync<WebSocketException>(
            async () => await ws.ConnectAsync(new Uri($"wss://{Host}/other"), cts.Token));
    }

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), FrameworkWsMessageType.Text, endOfMessage: true, ct).AsTask();

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var result = await ws.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
