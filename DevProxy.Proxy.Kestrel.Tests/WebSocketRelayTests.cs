// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using DevProxy.Proxy.Kestrel.Internal;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class WebSocketRelayTests
{
    // ── ParseResponseHead (pure) ────────────────────────────────────────────

    [Fact]
    public void ParseResponseHead_Parses101AndHeaders()
    {
        var (status, reason, headers) = WebSocketRelay.ParseResponseHead(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");

        Assert.Equal(101, status);
        Assert.Equal("Switching Protocols", reason);
        Assert.Equal("websocket", headers.GetFirst("Upgrade")?.Value);
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", headers.GetFirst("Sec-WebSocket-Accept")?.Value);
    }

    [Fact]
    public void ParseResponseHead_ParsesStatusWithoutReason()
    {
        var (status, reason, _) = WebSocketRelay.ParseResponseHead("HTTP/1.1 204\r\n");

        Assert.Equal(204, status);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ParseResponseHead_Throws_OnMalformedStatusLine()
    {
        _ = Assert.Throws<InvalidOperationException>(() => WebSocketRelay.ParseResponseHead("garbage"));
    }

    // ── End-to-end relay over loopback ──────────────────────────────────────

    [Fact]
    public async Task RelayAsync_ReplaysHandshake_RelaysFrames_AndReportsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Fake origin: capture the replayed handshake, answer 101, exchange WebSocket messages.
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var originHandshakeText = new TaskCompletionSource<string>();
        var originTask = RunFakeOriginAsync(originListener, originHandshakeText, cts.Token);

        // The proxy holds proxySide; the test plays the client on clientSide.
        var (clientSide, proxySide) = await TestSockets.ConnectedPairAsync();

        var headers = new HeaderCollection();
        headers.Add("Host", $"127.0.0.1:{originPort}");
        headers.Add("Upgrade", "websocket");
        headers.Add("Connection", "Upgrade");
        headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        headers.Add("Proxy-Connection", "keep-alive"); // must be stripped from the replay
        var request = new MutableHttpRequest(
            "GET", new Uri($"http://127.0.0.1:{originPort}/chat?room=1"), HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty);

        MutableHttpResponse? observed = null;
        var capturedMessages = new ConcurrentQueue<WebSocketMessageRecord>();
        var relay = new WebSocketRelay(NullLogger.Instance);
        var relayTask = relay.RelayAsync(
            proxySide, request, request.RequestUri,
            r => { observed = r; return Task.CompletedTask; },
            msg => capturedMessages.Enqueue(msg),
            messageInterceptor: null, onConnected: null, cts.Token);

        // Client reads the 101 handshake the proxy wrote back.
        var handshakeBack = await ReadUntilDoubleCrlfAsync(clientSide, cts.Token);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", handshakeBack, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept: abc123", handshakeBack, StringComparison.Ordinal);

        // Wrap client side as a WebSocket to exchange proper frames.
        using var clientWs = WebSocket.CreateFromStream(
            clientSide, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });

        // Receive "origin-frame" sent by the fake origin.
        var receiveBuffer = new byte[1024];
        var receiveResult = await clientWs.ReceiveAsync(receiveBuffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, receiveResult.MessageType);
        Assert.Equal("origin-frame", Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count));

        // Client → origin WebSocket message.
        await clientWs.SendAsync(
            Encoding.UTF8.GetBytes("client-frame"), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        await clientWs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var replayed = await originHandshakeText.Task;
        // Replayed in origin-form, preserving the WebSocket headers, dropping proxy ones.
        Assert.StartsWith("GET /chat?room=1 HTTP/1.1", replayed, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket", replayed, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", replayed, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Connection", replayed, StringComparison.Ordinal);

        // onHandshakeResponse saw the parsed 101.
        Assert.NotNull(observed);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, observed!.StatusCode);

        var echoed = await originTask; // origin returns what it received
        Assert.Equal("client-frame", echoed);

        proxySide.Dispose();
        await relayTask;

        // Verify captured messages (origin→client "origin-frame", client→origin "client-frame", close).
        var messages = capturedMessages.ToArray();
        Assert.True(messages.Length >= 2, $"Expected at least 2 messages, got {messages.Length}");
        Assert.Equal(WebSocketMessageDirection.Receive, messages[0].Direction);
        Assert.Equal("origin-frame", messages[0].Text);
        Assert.Equal(WebSocketMessageDirection.Send, messages[1].Direction);
        Assert.Equal("client-frame", messages[1].Text);
    }

    // Fake origin: read the request head, reply 101, send a text message, receive one back.
    private static async Task<string> RunFakeOriginAsync(
        TcpListener listener, TaskCompletionSource<string> handshakeText, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var head = await ReadUntilDoubleCrlfAsync(stream, ct);
        handshakeText.SetResult(head);

        var responseHead = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: abc123\r\n\r\n");
        await stream.WriteAsync(responseHead, ct);
        await stream.FlushAsync(ct);

        // Use a proper WebSocket to exchange frames.
        using var ws = WebSocket.CreateFromStream(
            stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        await ws.SendAsync(
            Encoding.UTF8.GetBytes("origin-frame"), WebSocketMessageType.Text, endOfMessage: true, ct);

        var buffer = new byte[1024];
        var result = await ws.ReceiveAsync(buffer, ct);
        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);

        // Wait for close from client, then close our side.
        var closeResult = await ws.ReceiveAsync(buffer, ct);
        if (closeResult.MessageType == WebSocketMessageType.Close)
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        }

        return received;
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
