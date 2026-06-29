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
/// <see cref="ForwardingInvariants"/>: hop-by-hop headers are stripped, <c>Expect</c>
/// is dropped (already satisfied at the proxy), the body is delivered to plugins
/// decompressed, and framing headers are recomputed on write-back.
///
/// <para>
/// Most responses are fully buffered (<see cref="OriginResponse.IsStreaming"/> false).
/// A <c>text/event-stream</c> response is left UNBUFFERED instead — its body stream
/// stays open in the returned <see cref="OriginResponse"/> so the caller can forward
/// it to the client incrementally (Server-Sent Events). Buffering such a stream would
/// withhold every event until the stream ends, and an unbounded one would never end.
/// </para>
/// </summary>
internal sealed class UpstreamForwarder(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<OriginResponse> ForwardAsync(IHttpRequest request, CancellationToken ct)
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
                || string.Equals(header.Name, "Expect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!outgoing.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                _ = content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        HttpResponseMessage? originResponse = await _httpClient
            .SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        try
        {
            var headers = new HeaderCollection();
            CopyHeaders(originResponse.Headers, headers);
            CopyHeaders(originResponse.Content.Headers, headers);

            // A HEAD response has no body but reports the Content-Length a GET would —
            // keep it so the client sees the real resource size (RFC 9110 §9.3.2). It
            // can never take the streaming path (there is nothing to stream).
            var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
            var isStreaming = !isHead && IsEventStream(headers);

            // Body is (or will be) delivered decompressed; advertise nothing stale —
            // a buffered body gets a real Content-Length on write-back, a streamed one
            // is re-framed as chunked. HEAD keeps the origin's Content-Length.
            _ = headers.Remove("Content-Encoding");
            _ = headers.Remove("Transfer-Encoding");
            if (!isHead)
            {
                _ = headers.Remove("Content-Length");
            }

            if (isStreaming)
            {
                // Leave the body unbuffered: hand the live stream to the caller and
                // transfer ownership of the response message so it stays open until the
                // stream is fully relayed.
                var bodyStream = await originResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var streamingResponse = new MutableHttpResponse(
                    originResponse.StatusCode,
                    originResponse.Version,
                    headers,
                    ReadOnlyMemory<byte>.Empty,
                    originResponse.ReasonPhrase);

                var result = new OriginResponse(streamingResponse, bodyStream, originResponse);
                originResponse = null;
                return result;
            }

            // AutomaticDecompression on the shared handler means the bytes here are
            // already decompressed; the Content-Encoding header is removed for us.
            var body = await originResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var response = new MutableHttpResponse(
                originResponse.StatusCode,
                originResponse.Version,
                headers,
                body,
                originResponse.ReasonPhrase);

            return new OriginResponse(response, bodyStream: null, message: null);
        }
        finally
        {
            originResponse?.Dispose();
        }
    }

    private static bool IsEventStream(HeaderCollection headers) =>
        headers.GetFirst("Content-Type")?.Value
            .Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

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

/// <summary>
/// The origin response projected onto the canonical model, owning the lifetime of the
/// underlying <see cref="HttpResponseMessage"/> when the body is streamed.
///
/// <para>
/// Buffered case (<see cref="IsStreaming"/> false): <see cref="Response"/> already
/// carries the full body; <see cref="BodyStream"/> is null and there is nothing to
/// dispose.
/// </para>
/// <para>
/// Streaming case (<see cref="IsStreaming"/> true): <see cref="Response"/> carries
/// headers only and <see cref="BodyStream"/> is the live, decompressed origin body the
/// caller must pump to the client. The response message stays open until this is
/// disposed.
/// </para>
/// </summary>
internal sealed class OriginResponse(MutableHttpResponse response, Stream? bodyStream, HttpResponseMessage? message)
    : IAsyncDisposable
{
    private readonly HttpResponseMessage? _message = message;

    public MutableHttpResponse Response { get; } = response;

    public Stream? BodyStream { get; } = bodyStream;

    public bool IsStreaming => BodyStream is not null;

    public async ValueTask DisposeAsync()
    {
        if (BodyStream is not null)
        {
            await BodyStream.DisposeAsync().ConfigureAwait(false);
        }
        _message?.Dispose();
    }
}
