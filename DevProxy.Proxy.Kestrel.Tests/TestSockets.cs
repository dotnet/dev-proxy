// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;

namespace DevProxy.Proxy.Kestrel.Tests;

/// <summary>
/// Loopback-socket helpers for exercising the raw stream relays (blind tunnel /
/// WebSocket) end-to-end without mocking <see cref="Stream"/> semantics.
/// </summary>
internal static class TestSockets
{
    /// <summary>
    /// Creates a connected pair of TCP streams over the loopback interface. Writing to
    /// one end is readable on the other. Dispose both streams (and they own their
    /// sockets) when done.
    /// </summary>
    public static async Task<(NetworkStream Left, NetworkStream Right)> ConnectedPairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connectTask = ConnectAsync(port);
        var accepted = await listener.AcceptTcpClientAsync();
        var connected = await connectTask;

        return (connected.GetStream(), accepted.GetStream());

        static async Task<TcpClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return client;
        }
    }
}

/// <summary>
/// A read-only stream that returns one scripted segment per <c>ReadAsync</c> call,
/// simulating an origin that emits its body in discrete pieces (e.g. SSE events). Lets
/// tests assert that each piece is forwarded as its own chunk rather than coalesced.
/// </summary>
internal sealed class ScriptedReadStream(IReadOnlyList<byte[]> segments) : Stream
{
    private int _index;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_index >= segments.Count)
        {
            return ValueTask.FromResult(0);
        }

        var segment = segments[_index++];
        if (segment.Length > buffer.Length)
        {
            throw new InvalidOperationException("Test buffer smaller than a scripted segment.");
        }

        segment.CopyTo(buffer.Span);
        return ValueTask.FromResult(segment.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
