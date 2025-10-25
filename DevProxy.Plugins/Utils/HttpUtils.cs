// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;

namespace DevProxy.Plugins.Utils;

internal sealed class HttpUtils
{
    public static string GetBodyFromStreamingResponse(Response response, ILogger logger)
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

    public static bool IsStreamingResponse(Response response, ILogger logger)
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