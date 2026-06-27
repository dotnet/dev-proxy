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
/// <para>Slice-1 scope: writes <c>Connection: close</c> and one body buffer.
/// Keep-alive + chunked write-back are tracked hardening.</para>
/// </summary>
internal static class ResponseWriter
{
    public static async Task WriteAsync(Stream clientStream, IHttpResponse response, CancellationToken ct)
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
        _ = head.Append("Connection: close\r\n\r\n");

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (!body.IsEmpty)
        {
            await clientStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        await clientStream.FlushAsync(ct).ConfigureAwait(false);
    }
}
