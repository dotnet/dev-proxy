// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Serializes a canonical response back to the client. Recomputes
/// <c>Content-Length</c> from the (decompressed) body and strips hop-by-hop /
/// framing / encoding headers so the client always receives a valid message
/// (<see cref="ForwardingInvariants"/>).
///
/// <para>Non-streaming responses are buffered, so <c>Content-Length</c> is recomputed
/// and the client can frame the response unambiguously, allowing keep-alive when the
/// request permits. Streamed (<c>text/event-stream</c>) responses are re-framed as
/// chunked transfer by <see cref="StreamingResponseWriter"/> instead.</para>
/// </summary>
internal static class ResponseWriter
{
    private static readonly byte[] s_continue = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

    /// <summary>
    /// Writes an interim <c>100 Continue</c> so a client that sent
    /// <c>Expect: 100-continue</c> proceeds to send its request body. The proxy always
    /// intends to read the body (it buffers and forwards it), so it answers the
    /// expectation itself rather than round-tripping to the origin first.
    /// </summary>
    public static async Task WriteContinueAsync(Stream clientStream, CancellationToken ct)
    {
        await clientStream.WriteAsync(s_continue, ct).ConfigureAwait(false);
        await clientStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task WriteAsync(Stream clientStream, IHttpResponse response, bool keepAlive, CancellationToken ct)
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
                || string.Equals(header.Name, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _ = head.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }

        var body = response.Body;
        _ = head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        _ = head.Append(keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (!body.IsEmpty)
        {
            await clientStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        await clientStream.FlushAsync(ct).ConfigureAwait(false);
    }
}
