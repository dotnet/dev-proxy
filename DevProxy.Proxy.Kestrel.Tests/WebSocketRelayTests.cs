// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
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

        // Fake origin: capture the replayed handshake, answer 101 + a "frame", echo back.
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
        var relay = new WebSocketRelay(NullLogger.Instance);
        var relayTask = relay.RelayAsync(
            proxySide, request, request.RequestUri,
            r => { observed = r; return Task.CompletedTask; }, cts.Token);

        // Client reads the 101 handshake the proxy wrote back, then the origin's frame.
        var handshakeBack = await ReadUntilDoubleCrlfAsync(clientSide, cts.Token);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", handshakeBack, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept: abc123", handshakeBack, StringComparison.Ordinal);
        Assert.Equal("origin-frame", await ReadTextAsync(clientSide, "origin-frame".Length, cts.Token));

        // Client → origin frame is spliced through.
        await clientSide.WriteAsync(Encoding.ASCII.GetBytes("client-frame"), cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var replayed = await originHandshakeText.Task;
        // Replayed in origin-form, preserving the WebSocket headers, dropping proxy ones.
        Assert.StartsWith("GET /chat?room=1 HTTP/1.1", replayed, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket", replayed, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", replayed, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Connection", replayed, StringComparison.Ordinal);

        // onHandshakeResponse saw the parsed 101.
        Assert.NotNull(observed);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, observed!.StatusCode);

        var echoed = await originTask; // origin returns what it received after the handshake
        Assert.Equal("client-frame", echoed);

        clientSide.Dispose();
        proxySide.Dispose();
        await relayTask;
    }

    // Fake origin: read the request head, reply 101 + "origin-frame", then read one
    // post-handshake chunk and return it (so the test can assert client→origin splicing).
    private static async Task<string> RunFakeOriginAsync(
        TcpListener listener, TaskCompletionSource<string> handshakeText, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var head = await ReadUntilDoubleCrlfAsync(stream, ct);
        handshakeText.SetResult(head);

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: abc123\r\n\r\n" +
            "origin-frame");
        await stream.WriteAsync(response, ct);
        await stream.FlushAsync(ct);

        return await ReadTextAsync(stream, "client-frame".Length, ct);
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

    private static async Task<string> ReadTextAsync(Stream stream, int byteCount, CancellationToken ct)
    {
        var buffer = new byte[byteCount];
        var offset = 0;
        while (offset < byteCount)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return Encoding.ASCII.GetString(buffer, 0, offset);
    }
}
