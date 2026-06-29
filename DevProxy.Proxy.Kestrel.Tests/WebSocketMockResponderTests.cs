// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

/// <summary>
/// Exercises <see cref="WebSocketMockResponder"/> end-to-end over a loopback socket pair:
/// the responder plays the WebSocket SERVER (proxy as mock), a real framework
/// <see cref="ClientWebSocket"/>-equivalent (<see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/>
/// in client mode) plays the CLIENT. Proves the handshake (101 + correct
/// <c>Sec-WebSocket-Accept</c>) and the scripted on-connect / reactive / close flow.
///
/// <code>
///   client (test)                    responder (under test)
///   ─────────────                    ──────────────────────
///   read 101 + verify Accept  ◀──────  write 101 verbatim
///   recv "welcome"            ◀──────  handler: SendText("welcome")
///   send "ping"              ──────▶   handler: recv → SendText("pong")
///   recv "pong"              ◀──────
///   send "hi"               ──────▶   handler: recv → SendText("echo:hi")
///   recv "echo:hi"          ◀──────
///   Close                   ──────▶   handler: recv Close → break → CloseAsync
///   recv Close              ◀──────
/// </code>
/// </summary>
public class WebSocketMockResponderTests
{
    // RFC 6455 §1.3 worked example: key "dGhlIHNhbXBsZSBub25jZQ==" → this accept token.
    private const string SampleKey = "dGhlIHNhbXBsZSBub25jZQ==";
    private const string ExpectedAccept = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

    [Fact]
    public async Task RespondAsync_CompletesHandshake_AndRunsScriptedExchange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (clientSide, proxySide) = await TestSockets.ConnectedPairAsync();

        var request = BuildUpgradeRequest(SampleKey, subProtocol: null);

        // Scripted server handler: greet, then echo with a "pong" special-case until close.
        static async Task Handler(IWebSocketConnection conn, CancellationToken ct)
        {
            await conn.SendTextAsync("welcome", ct);
            while (true)
            {
                var msg = await conn.ReceiveAsync(ct);
                if (msg is null || msg.Type == WebSocketMessageType.Close)
                {
                    break;
                }
                await conn.SendTextAsync(
                    msg.Text == "ping" ? "pong" : $"echo:{msg.Text}", ct);
            }
        }

        MutableHttpResponse? observedHandshake = null;
        var responder = new WebSocketMockResponder(NullLogger.Instance);
        var serverTask = responder.RespondAsync(
            proxySide, request, Handler,
            r => { observedHandshake = r; return Task.CompletedTask; },
            cts.Token);

        // ── client: read + verify the raw 101 (one byte at a time, no over-read) ──
        var head = await ReadUntilDoubleCrlfAsync(clientSide, cts.Token);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", head, StringComparison.Ordinal);
        Assert.Contains($"Sec-WebSocket-Accept: {ExpectedAccept}", head, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket", head, StringComparison.Ordinal);

        // onHandshakeResponse fired with the parsed 101 (so the pipeline/req-log can run).
        Assert.NotNull(observedHandshake);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, observedHandshake!.StatusCode);

        // ── client: drive a real WebSocket over the remaining stream ──
        using var clientWs = WebSocket.CreateFromStream(
            clientSide, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

        Assert.Equal("welcome", await ReceiveTextAsync(clientWs, cts.Token));

        await SendTextAsync(clientWs, "ping", cts.Token);
        Assert.Equal("pong", await ReceiveTextAsync(clientWs, cts.Token));

        await SendTextAsync(clientWs, "hi", cts.Token);
        Assert.Equal("echo:hi", await ReceiveTextAsync(clientWs, cts.Token));

        // Client closes; the responder observes Close, ends the handler, and closes back.
        await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cts.Token);

        await serverTask;
        Assert.Equal(WebSocketState.Closed, clientWs.State);

        clientSide.Dispose();
        proxySide.Dispose();
    }

    [Fact]
    public async Task RespondAsync_EchoesRequestedSubProtocol()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientSide, proxySide) = await TestSockets.ConnectedPairAsync();

        var request = BuildUpgradeRequest(SampleKey, subProtocol: "chat, superchat");

        static async Task Handler(IWebSocketConnection conn, CancellationToken ct) =>
            await conn.SendTextAsync("hi", ct);

        var responder = new WebSocketMockResponder(NullLogger.Instance);
        var serverTask = responder.RespondAsync(
            proxySide, request, Handler, _ => Task.CompletedTask, cts.Token);

        var head = await ReadUntilDoubleCrlfAsync(clientSide, cts.Token);
        // RFC 6455 §4.2.2: only the FIRST offered sub-protocol is echoed.
        Assert.Contains("Sec-WebSocket-Protocol: chat", head, StringComparison.Ordinal);
        Assert.DoesNotContain("superchat", head, StringComparison.Ordinal);

        using var clientWs = WebSocket.CreateFromStream(
            clientSide, isServer: false, subProtocol: "chat", keepAliveInterval: TimeSpan.FromSeconds(30));
        Assert.Equal("hi", await ReceiveTextAsync(clientWs, cts.Token));

        await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        await serverTask;

        clientSide.Dispose();
        proxySide.Dispose();
    }

    [Fact]
    public async Task RespondAsync_RefusesHandshake_WhenKeyMissing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientSide, proxySide) = await TestSockets.ConnectedPairAsync();

        var headers = new HeaderCollection();
        headers.Add("Host", "ws.example.test");
        headers.Add("Upgrade", "websocket");
        headers.Add("Connection", "Upgrade");
        // No Sec-WebSocket-Key.
        var request = new MutableHttpRequest(
            "GET", new Uri("http://ws.example.test/socket"), HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty);

        var handlerRan = false;
        Task Handler(IWebSocketConnection conn, CancellationToken ct) { handlerRan = true; return Task.CompletedTask; }

        var responder = new WebSocketMockResponder(NullLogger.Instance);
        var serverTask = responder.RespondAsync(
            proxySide, request, Handler, _ => Task.CompletedTask, cts.Token);

        var head = await ReadUntilDoubleCrlfAsync(clientSide, cts.Token);
        Assert.StartsWith("HTTP/1.1 400 Bad Request", head, StringComparison.Ordinal);
        Assert.False(handlerRan);

        await serverTask;
        clientSide.Dispose();
        proxySide.Dispose();
    }

    [Fact]
    public void ComputeAcceptKey_MatchesRfc6455Example()
    {
        // Independent recomputation of the well-known example, guarding the magic GUID.
        // SHA1 is mandated by the WebSocket handshake (RFC 6455 §4.2.2).
#pragma warning disable CA5350
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(SampleKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
#pragma warning restore CA5350
        Assert.Equal(ExpectedAccept, Convert.ToBase64String(hash));
    }

    private static MutableHttpRequest BuildUpgradeRequest(string key, string? subProtocol)
    {
        var headers = new HeaderCollection();
        headers.Add("Host", "ws.example.test");
        headers.Add("Upgrade", "websocket");
        headers.Add("Connection", "Upgrade");
        headers.Add("Sec-WebSocket-Key", key);
        headers.Add("Sec-WebSocket-Version", "13");
        if (subProtocol is not null)
        {
            headers.Add("Sec-WebSocket-Protocol", subProtocol);
        }
        return new MutableHttpRequest(
            "GET", new Uri("http://ws.example.test/socket"), HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty);
    }

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var result = await ws.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task<string> ReadUntilDoubleCrlfAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4
                && bytes[^4] == (byte)'\r' && bytes[^3] == (byte)'\n'
                && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
