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

    public static async Task WriteAsync(
        Stream clientStream, IHttpResponse response, bool keepAlive, string requestMethod, CancellationToken ct)
    {
        // A response to HEAD carries the same headers a GET would — including the
        // resource's Content-Length — but never a message body (RFC 9110 §9.3.2).
        // For every other method the body IS the response, so Content-Length is
        // recomputed from it and any stale origin value is dropped.
        var isHead = string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

        var head = new StringBuilder();
        var statusCode = (int)response.StatusCode;
        var reason = string.IsNullOrEmpty(response.StatusDescription)
            ? ReasonPhrases.GetReasonPhrase(statusCode)
            : response.StatusDescription;

        _ = head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {reason}\r\n");

        string? preservedContentLength = null;
        foreach (var header in response.Headers)
        {
            if (ForwardingInvariants.HopByHopHeaders.Contains(header.Name)
                || string.Equals(header.Name, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                // For HEAD, keep the origin's declared length (the size a GET would
                // return); otherwise it is recomputed below from the actual body.
                if (isHead)
                {
                    preservedContentLength ??= header.Value;
                }
                continue;
            }
            _ = head.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }

        var body = response.Body;
        var contentLength = isHead
            ? preservedContentLength ?? body.Length.ToString(CultureInfo.InvariantCulture)
            : body.Length.ToString(CultureInfo.InvariantCulture);
        _ = head.Append(CultureInfo.InvariantCulture, $"Content-Length: {contentLength}\r\n");
        _ = head.Append(keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (!isHead && !body.IsEmpty)
        {
            await clientStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        await clientStream.FlushAsync(ct).ConfigureAwait(false);
    }
}
