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
