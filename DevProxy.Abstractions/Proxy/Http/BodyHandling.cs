// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// How the engine handles a message body for one exchange. The effective mode is
/// resolved per exchange from the union of <see cref="BodyCapabilities"/> declared
/// by the plugins whose URL filter matches, plus the body's size/streaming shape.
/// See <see cref="BodyModeResolver"/>.
///
/// <code>
///  no full-body need ──► CanStreamInspect? ──yes─► TeeForInspection
///        │                      └──no──────────────► StreamingPassThrough
///        ▼ (full body needed / mutation)
///  unbounded stream? ──yes─► degrade (Tee or PassThrough)
///        ▼ no
///  fits in memory?  ──yes─► BufferedInMemory
///        ▼ no
///  fits on disk?    ──yes─► SpooledToDisk
///        ▼ no
///  degrade (Tee or PassThrough)
/// </code>
/// </summary>
public enum BodyMode
{
    /// <summary>
    /// Bytes flow client↔origin without retention. Plugins cannot read or mutate
    /// the body. Required for unbounded streams (SSE, long-poll) and oversized
    /// payloads. This is the safe default.
    /// </summary>
    StreamingPassThrough,

    /// <summary>
    /// The full body is buffered in memory; plugins can read and mutate it. Only
    /// chosen for bounded bodies within the in-memory limit.
    /// </summary>
    BufferedInMemory,

    /// <summary>
    /// The full body is spooled to a temporary file; plugins can read and mutate
    /// large but finite bodies without exhausting RAM.
    /// </summary>
    SpooledToDisk,

    /// <summary>
    /// Bytes stream through unbuffered while a copy is delivered to read-only
    /// inspectors (e.g. logging). No mutation; the body is not materialized on the
    /// forwarding path.
    /// </summary>
    TeeForInspection,

    /// <summary>
    /// The connection is upgraded (WebSocket) or blind-tunnelled. Opaque byte
    /// relay with no HTTP body semantics.
    /// </summary>
    UpgradedRaw,
}

/// <summary>
/// Which side of an exchange a body belongs to. Selects the relevant
/// <see cref="BodyCapabilities"/> flag during resolution.
/// </summary>
public enum BodyDirection
{
    /// <summary>The request body (client → origin).</summary>
    Request,

    /// <summary>The response body (origin → client).</summary>
    Response,
}

/// <summary>
/// Declares what a plugin needs to do with message bodies. The engine aggregates
/// these across all matching plugins to pick a <see cref="BodyMode"/> per exchange,
/// reconciling "stream SSE unbuffered" with "let plugins read full bodies".
/// </summary>
[Flags]
public enum BodyCapabilities
{
    /// <summary>The plugin does not touch bodies. Compatible with streaming.</summary>
    None = 0,

    /// <summary>The plugin reads the complete request body before forwarding.</summary>
    NeedsFullRequestBody = 1 << 0,

    /// <summary>The plugin reads the complete response body before forwarding.</summary>
    NeedsFullResponseBody = 1 << 1,

    /// <summary>
    /// The plugin can inspect body bytes incrementally as they stream (read-only),
    /// without requiring the whole body at once.
    /// </summary>
    CanStreamInspect = 1 << 2,

    /// <summary>The plugin may modify body bytes (implies the body must be buffered).</summary>
    CanMutate = 1 << 3,

    /// <summary>
    /// The plugin must not be run against unbounded streams (it would otherwise
    /// buffer forever). On such exchanges the plugin is skipped and the engine
    /// degrades to a streaming mode.
    /// </summary>
    CannotRunOnInfiniteStreams = 1 << 4,
}
