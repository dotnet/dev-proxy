// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Decides how an exception thrown while forwarding a request to the origin should be
/// surfaced to the client. The key subtlety is that an <see cref="HttpClient"/> request
/// timeout throws <see cref="TaskCanceledException"/> — which derives from
/// <see cref="OperationCanceledException"/>, the same type a genuine client-driven
/// cancellation produces. They are told apart by whether the connection's own token was
/// cancelled: if it was, the client went away (silent teardown); if it was not, the
/// origin timed out and the client deserves a gateway error.
///
/// <code>
///   forward throws
///        │
///        ├─ OperationCanceledException ──┬─ client token cancelled ─► null (silent teardown)
///        │                               └─ token NOT cancelled ─────► 504 Gateway Timeout
///        │
///        └─ anything else (TLS failure, ─────────────────────────────► 502 Bad Gateway
///           DNS, connection refused, …)
/// </code>
/// </summary>
internal static class UpstreamFailure
{
    /// <summary>
    /// Maps a forward exception to the status/message the client should see, or
    /// <see langword="null"/> when it is a genuine client cancellation that should be
    /// treated as a silent connection teardown (no response written).
    /// </summary>
    public static (HttpStatusCode Status, string Message)? Classify(Exception exception, bool clientCancelled)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return clientCancelled
                ? null
                : (HttpStatusCode.GatewayTimeout, "Upstream request timed out");
        }

        return (HttpStatusCode.BadGateway, "Upstream request failed");
    }
}
