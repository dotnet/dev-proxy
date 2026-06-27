// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Splices two streams together, copying bytes verbatim in both directions until
/// either side closes — the raw relay shared by the blind tunnel (non-watched TLS)
/// and the WebSocket frame relay (after a <c>101</c> handshake).
///
/// <code>
///   a ──────────────►──────────────► b
///     ◄──────────────◄──────────────
///   When EITHER direction ends, the other is cancelled; the method returns only
///   after BOTH copy tasks have finished, so neither outlives the streams it uses
///   (CA2025).
/// </code>
/// </summary>
internal static class StreamRelay
{
    public static async Task RelayBidirectionalAsync(Stream a, Stream b, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
#pragma warning disable CA2025 // The finally awaits both copies before the streams are disposed by the caller.
        var aToB = a.CopyToAsync(b, linked.Token);
        var bToA = b.CopyToAsync(a, linked.Token);

        try
        {
            _ = await Task.WhenAny(aToB, bToA).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Task.WhenAll(aToB, bToA).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // Either peer closed the connection — normal relay teardown.
            }
        }
#pragma warning restore CA2025
    }
}
