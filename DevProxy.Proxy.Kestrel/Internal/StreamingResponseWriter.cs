// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Writes a streaming (<c>text/event-stream</c>) response to the client incrementally,
/// re-framing the origin's decompressed body as HTTP/1.1 chunked transfer so each piece
/// reaches the client as it arrives rather than all-at-once at the end.
///
/// <para>
/// While pumping, a capped copy of the body is accumulated and returned so a watched
/// session's read-only <c>AfterResponse</c> inspectors (e.g. OpenAI telemetry) still see
/// the complete stream. If the body exceeds the cap — a long-lived or unbounded stream —
/// accumulation is abandoned (empty is returned) but relaying continues uninterrupted, so
/// the engine never hangs or exhausts memory on an infinite stream.
/// </para>
///
/// <code>
///   origin body stream                     client (chunked)
///   ─────────────────                       ────────────────
///   read N bytes ──┬─ write "{N:X}\r\n…\r\n" + FLUSH ──► event delivered now
///                  └─ append to cap'd copy (until cap)
///   …repeat until EOF…
///   EOF ──────────── write "0\r\n\r\n" ───────────────► stream terminated
/// </code>
/// </summary>
internal static class StreamingResponseWriter
{
    private static readonly byte[] s_lastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly byte[] s_crlf = Encoding.ASCII.GetBytes("\r\n");

    /// <summary>
    /// Streams <paramref name="originBody"/> to <paramref name="clientStream"/> as chunked
    /// transfer and returns the accumulated body (empty when accumulation is disabled or
    /// the body exceeded <paramref name="accumulateCap"/>).
    /// </summary>
    /// <param name="accumulateCap">
    /// Maximum bytes to retain for inspectors. Pass <c>0</c> to disable accumulation
    /// (pure pass-through, e.g. for non-watched traffic).
    /// </param>
    public static async Task<ReadOnlyMemory<byte>> WriteAsync(
        Stream clientStream,
        IHttpResponse response,
        Stream originBody,
        bool keepAlive,
        int accumulateCap,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(originBody);

        await WriteHeadAsync(clientStream, response, keepAlive, ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var accumulator = accumulateCap > 0 ? new ArrayBufferWriter<byte>() : null;
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await originBody.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await WriteChunkAsync(clientStream, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);

                if (accumulator is not null && !truncated)
                {
                    if (accumulator.WrittenCount + read <= accumulateCap)
                    {
                        accumulator.Write(buffer.AsSpan(0, read));
                    }
                    else
                    {
                        // Exceeded what we are willing to hold for inspectors. Stop
                        // accumulating but keep relaying — a partial body would mislead
                        // inspectors, so it is dropped entirely.
                        truncated = true;
                    }
                }
            }

            await clientStream.WriteAsync(s_lastChunk, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return accumulator is null || truncated ? ReadOnlyMemory<byte>.Empty : accumulator.WrittenMemory;
    }

    private static async Task WriteHeadAsync(Stream clientStream, IHttpResponse response, bool keepAlive, CancellationToken ct)
    {
        var head = new StringBuilder();
        var statusCode = (int)response.StatusCode;
        var reason = string.IsNullOrEmpty(response.StatusDescription)
            ? ReasonPhrases.GetReasonPhrase(statusCode)
            : response.StatusDescription;

        _ = head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {reason}\r\n");

        foreach (var header in response.Headers)
        {
            if (ForwardingInvariants.HopByHopHeaders.Contains(header.Name)
                || string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _ = head.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }

        _ = head.Append("Transfer-Encoding: chunked\r\n");
        _ = head.Append(keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await clientStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteChunkAsync(Stream clientStream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var sizeLine = Encoding.ASCII.GetBytes(
            data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        await clientStream.WriteAsync(sizeLine, ct).ConfigureAwait(false);
        await clientStream.WriteAsync(data, ct).ConfigureAwait(false);
        await clientStream.WriteAsync(s_crlf, ct).ConfigureAwait(false);
    }
}
