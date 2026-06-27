// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Forwards a canonical request to its origin with <see cref="HttpClient"/> and
/// projects the origin response back onto the canonical model. Honors the
/// <see cref="ForwardingInvariants"/>: hop-by-hop headers are stripped, the body
/// is delivered to plugins decompressed, and framing headers are recomputed on
/// write-back.
/// </summary>
internal sealed class UpstreamForwarder(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<MutableHttpResponse> ForwardAsync(IHttpRequest request, CancellationToken ct)
    {
        using var outgoing = new HttpRequestMessage(new HttpMethod(request.Method), request.RequestUri);

        ByteArrayContent? content = null;
        if (request.HasBody)
        {
            content = new ByteArrayContent(request.Body.ToArray());
            outgoing.Content = content;
        }

        foreach (var header in request.Headers)
        {
            if (IsHopByHop(header.Name)
                || string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!outgoing.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                _ = content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        var originResponse = await _httpClient
            .SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        try
        {
            // AutomaticDecompression on the shared handler means the bytes here are
            // already decompressed; the Content-Encoding header is removed for us.
            var body = await originResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var headers = new HeaderCollection();
            CopyHeaders(originResponse.Headers, headers);
            CopyHeaders(originResponse.Content.Headers, headers);

            var response = new MutableHttpResponse(
                originResponse.StatusCode,
                originResponse.Version,
                headers,
                body,
                originResponse.ReasonPhrase);

            // Body is already decompressed; advertise its real length and drop any
            // stale framing/encoding the origin declared.
            _ = headers.Remove("Content-Encoding");
            _ = headers.Remove("Content-Length");
            _ = headers.Remove("Transfer-Encoding");

            return response;
        }
        finally
        {
            originResponse.Dispose();
        }
    }

    private static void CopyHeaders(System.Net.Http.Headers.HttpHeaders source, HeaderCollection destination)
    {
        foreach (var header in source)
        {
            if (IsHopByHop(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                destination.Add(header.Key, value);
            }
        }
    }

    private static bool IsHopByHop(string name) => ForwardingInvariants.HopByHopHeaders.Contains(name);
}
