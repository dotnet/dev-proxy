// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Frozen;

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// The forwarding contract every engine adapter must honor so that plugins see a
/// consistent model regardless of the underlying proxy engine. These are the
/// rules that, if violated, silently corrupt traffic — documented here as the
/// single source of truth and partially enforced via the shared
/// <see cref="HopByHopHeaders"/> set.
///
/// <code>
///   client ──request──►  [strip hop-by-hop] ─► [plugins] ─► [re-add framing] ──► origin
///   client ◄─response──  [re-add framing]   ◄─ [plugins] ◄─ [strip hop-by-hop] ◄── origin
/// </code>
///
/// <list type="number">
/// <item>
/// <b>Hop-by-hop headers</b> (<see cref="HopByHopHeaders"/>) are connection-scoped
/// and MUST be stripped before forwarding, never copied end-to-end. Any header
/// named in a request's <c>Connection</c> header is also hop-by-hop for that message.
/// </item>
/// <item>
/// <b>Content-Length / Transfer-Encoding.</b> After a plugin mutates a body the
/// engine MUST recompute <c>Content-Length</c> (or switch to chunked) and ensure
/// exactly one framing mechanism is present — never both, to avoid request smuggling.
/// </item>
/// <item>
/// <b>Host.</b> The outgoing <c>Host</c> header MUST match the forwarded
/// <c>RequestUri</c> authority after any redirect/rewrite.
/// </item>
/// <item>
/// <b>Set-Cookie.</b> Multiple <c>Set-Cookie</c> headers MUST be preserved as
/// separate headers and never folded into one comma-joined value.
/// </item>
/// <item>
/// <b>Decompressed bodies.</b> Plugins always observe decompressed payloads. The
/// engine decodes <c>Content-Encoding</c> on read; on write-back it MUST re-encode
/// (or drop the encoding and fix <c>Content-Encoding</c>/<c>Content-Length</c>)
/// so the client still receives a valid message. See <see cref="IHttpMessage"/>.
/// </item>
/// <item>
/// <b>Upstream HTTP version.</b> Requests are forwarded as HTTP/1.1. h2 clients are
/// negotiated down to HTTP/1.1 via ALPN; h2-only clients are blind-tunnelled and
/// never reach this forwarding path.
/// </item>
/// </list>
/// </summary>
public static class ForwardingInvariants
{
    /// <summary>
    /// Connection-scoped headers that must be stripped before forwarding a message
    /// (RFC 9110 §7.6.1). Case-insensitive.
    /// </summary>
    public static FrozenSet<string> HopByHopHeaders { get; } = new[]
    {
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "TE",
        "Trailer",
        "Upgrade",
        "Proxy-Authenticate",
        "Proxy-Authorization",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
