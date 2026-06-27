// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DevProxy.Plugins.Utils;

internal sealed class HttpUtils
{
    // Decodes an HTTP message body (request or response) to a string.
    // If the Content-Type header specifies a charset, that encoding is used.
    // Otherwise, tries strict UTF-8 decoding first. If the body contains
    // invalid UTF-8 sequences, falls back to Latin-1 (ISO-8859-1) which is a
    // lossless 1:1 byte-to-char mapping that preserves raw byte values.
    // The underlying proxy library defaults to ISO-8859-1 per the obsolete
    // RFC 2616, but modern standards (RFC 7231, RFC 8259) treat UTF-8 as the
    // default for JSON and most web content.
    public static string GetBodyString(string? contentType, byte[] body)
    {
        if (contentType is not null)
        {
            try
            {
                var ct = new System.Net.Mime.ContentType(contentType);
                if (!string.IsNullOrEmpty(ct.CharSet))
                {
                    return Encoding.GetEncoding(ct.CharSet).GetString(body);
                }
            }
            catch
            {
                // Malformed Content-Type or unsupported charset; fall through
                // to UTF-8/Latin-1 default
            }
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(body);
        }
    }

    public static string GetBodyFromStreamingResponse(IHttpResponse response, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(GetBodyFromStreamingResponse));

        ArgumentNullException.ThrowIfNull(response);

        // default to the whole body
        var bodyString = response.BodyString;

        var chunks = bodyString.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (chunks.Length == 0)
        {
            logger.LogDebug("No chunks found in the response body");
            return bodyString;
        }

        // check if the last chunk is `data: [DONE]`
        var lastChunk = chunks.Last().Trim();
        if (lastChunk.Equals("data: [DONE]", StringComparison.OrdinalIgnoreCase))
        {
            // get next to last chunk
            var chunk = chunks.Length > 1 ? chunks[^2].Trim() : string.Empty;
            if (chunk.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                // remove the "data: " prefix
                bodyString = chunk["data: ".Length..].Trim();
                logger.LogDebug("Last chunk starts with 'data: ', using the last chunk as the body: {BodyString}", bodyString);
            }
            else
            {
                logger.LogDebug("Last chunk does not start with 'data: ', using the whole body");
            }
        }
        else
        {
            logger.LogDebug("Last chunk is not `data: [DONE]`, using the whole body");
        }

        logger.LogTrace("{Method} finished", nameof(GetBodyFromStreamingResponse));
        return bodyString;
    }

    public static bool IsStreamingResponse(IHttpResponse response, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(IsStreamingResponse));
        var contentType = response.Headers.FirstOrDefault(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(contentType))
        {
            logger.LogDebug("No content-type header found");
            return false;
        }

        var isStreamingResponse = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        logger.LogDebug("IsStreamingResponse: {IsStreamingResponse}", isStreamingResponse);

        logger.LogTrace("{Method} finished", nameof(IsStreamingResponse));
        return isStreamingResponse;
    }
}