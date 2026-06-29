// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using DevProxy.Proxy.Kestrel.Internal;

using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public class StreamRelayTests
{
    [Fact]
    public async Task RelayBidirectional_CopiesBothDirections()
    {
        // a1<->a2 is the "client" pair; b1<->b2 is the "origin" pair. The relay splices
        // a2<->b1, so bytes written to a1 surface on b2 and vice versa.
        var (a1, a2) = await TestSockets.ConnectedPairAsync();
        var (b1, b2) = await TestSockets.ConnectedPairAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var relay = StreamRelay.RelayBidirectionalAsync(a2, b1, cts.Token);

        await a1.WriteAsync(Encoding.ASCII.GetBytes("client-to-origin"), cts.Token);
        await a1.FlushAsync(cts.Token);
        Assert.Equal("client-to-origin", await ReadTextAsync(b2, "client-to-origin".Length, cts.Token));

        await b2.WriteAsync(Encoding.ASCII.GetBytes("origin-to-client"), cts.Token);
        await b2.FlushAsync(cts.Token);
        Assert.Equal("origin-to-client", await ReadTextAsync(a1, "origin-to-client".Length, cts.Token));

        // Closing one client end tears down the relay.
        a1.Dispose();
        await relay;

        a2.Dispose();
        b1.Dispose();
        b2.Dispose();
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
