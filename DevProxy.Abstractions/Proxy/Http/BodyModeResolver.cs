// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// Shape of a body as seen at resolution time, plus the engine's buffering limits.
/// </summary>
/// <param name="Direction">Which body (request/response) is being resolved.</param>
/// <param name="IsUpgrade">True for WebSocket upgrade / blind tunnel exchanges.</param>
/// <param name="IsUnboundedStream">
/// True when the body has no finite end the engine can rely on: <c>text/event-stream</c>,
/// chunked transfer with no declared length, or an explicitly streamed response.
/// </param>
/// <param name="ContentLength">Declared body length in bytes, when known.</param>
/// <param name="InMemoryLimitBytes">Largest body buffered in memory.</param>
/// <param name="SpoolLimitBytes">Largest body spooled to disk (must be ≥ in-memory limit).</param>
public readonly record struct BodyContext(
    BodyDirection Direction,
    bool IsUpgrade,
    bool IsUnboundedStream,
    long? ContentLength,
    long InMemoryLimitBytes = BodyModeResolver.DefaultInMemoryLimitBytes,
    long SpoolLimitBytes = BodyModeResolver.DefaultSpoolLimitBytes);

/// <summary>
/// Pure decision logic that maps the union of plugin <see cref="BodyCapabilities"/>
/// and a <see cref="BodyContext"/> to a single <see cref="BodyMode"/> for an
/// exchange. Deterministic and side-effect free so it can be unit-tested in
/// isolation and shared by every engine adapter.
/// </summary>
public static class BodyModeResolver
{
    /// <summary>Default maximum body size buffered in memory (4 MiB).</summary>
    public const long DefaultInMemoryLimitBytes = 4L * 1024 * 1024;

    /// <summary>Default maximum body size spooled to disk (256 MiB).</summary>
    public const long DefaultSpoolLimitBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Resolves the effective <see cref="BodyMode"/> for an exchange.
    /// </summary>
    /// <param name="aggregatedCapabilities">
    /// Bitwise-OR of the <see cref="BodyCapabilities"/> of every plugin whose URL
    /// filter matches this exchange.
    /// </param>
    /// <param name="context">The body's shape and the engine's limits.</param>
    public static BodyMode Resolve(BodyCapabilities aggregatedCapabilities, BodyContext context)
    {
        if (context.IsUpgrade)
        {
            return BodyMode.UpgradedRaw;
        }

        var needsFullBody = context.Direction == BodyDirection.Request
            ? aggregatedCapabilities.HasFlag(BodyCapabilities.NeedsFullRequestBody)
            : aggregatedCapabilities.HasFlag(BodyCapabilities.NeedsFullResponseBody);

        // Mutation implies the body must be buffered before write-back.
        needsFullBody |= aggregatedCapabilities.HasFlag(BodyCapabilities.CanMutate);

        var canStreamInspect = aggregatedCapabilities.HasFlag(BodyCapabilities.CanStreamInspect);

        if (!needsFullBody)
        {
            return canStreamInspect ? BodyMode.TeeForInspection : BodyMode.StreamingPassThrough;
        }

        // Full body wanted, but the stream may never end: never buffer unbounded.
        if (context.IsUnboundedStream)
        {
            return Degrade(canStreamInspect);
        }

        if (context.ContentLength is long length)
        {
            if (length <= context.InMemoryLimitBytes)
            {
                return BodyMode.BufferedInMemory;
            }

            if (length <= context.SpoolLimitBytes)
            {
                return BodyMode.SpooledToDisk;
            }

            // Bounded but larger than we are willing to spool.
            return Degrade(canStreamInspect);
        }

        // Bounded (not an unbounded stream) yet length unknown: spool defensively
        // rather than risk an unbounded in-memory buffer.
        return BodyMode.SpooledToDisk;
    }

    private static BodyMode Degrade(bool canStreamInspect) =>
        canStreamInspect ? BodyMode.TeeForInspection : BodyMode.StreamingPassThrough;
}
