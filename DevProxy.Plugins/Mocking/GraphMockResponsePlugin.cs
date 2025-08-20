// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Behavior;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Mocking;

public class GraphMockResponsePlugin(
    HttpClient httpClient,
    ILogger<GraphMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    MockResponsePlugin(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection,
        proxyStorage)
{
    public override string Name => nameof(GraphMockResponsePlugin);

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (Configuration.NoMocks)
        {
            Logger.LogRequest("Mocks are disabled", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (args.Request.RequestUri is null || !ProxyUtils.IsGraphBatchUrl(args.Request.RequestUri))
        {
            // not a batch request, use the basic mock functionality
            return await base.OnRequestAsync!(args, cancellationToken);
        }

        if (args.Request.Content is null)
        {
            return await base.OnRequestAsync!(args, cancellationToken);
        }

        var requestBody = await args.Request.Content.ReadAsStringAsync(cancellationToken);
        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(requestBody, ProxyUtils.JsonSerializerOptions);
        if (batch is null)
        {
            return await base.OnRequestAsync!(args, cancellationToken);
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            GraphBatchResponsePayloadResponse? response = null;
            var requestId = Guid.NewGuid().ToString();
            var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
            var headers = ProxyUtils
                .BuildGraphResponseHeaders(args.Request, requestId, requestDate);

            // Check for rate limiting headers from RateLimitingPlugin using new storage API
            var requestData = ProxyStorage.GetRequestData(args.RequestId);
            if (requestData.TryGetValue(nameof(RateLimitingPlugin), out var pluginData) &&
                pluginData is List<MockResponseHeader> rateLimitingHeaders)
            {
                ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
            }

            var mockResponse = GetMatchingMockResponse(request, args.Request.RequestUri);
            if (mockResponse == null)
            {
                response = new()
                {
                    Id = request.Id,
                    Status = (int)HttpStatusCode.BadGateway,
                    Headers = headers.ToDictionary(h => h.Name, h => h.Value),
                    Body = new GraphBatchResponsePayloadResponseBody
                    {
                        Error = new()
                        {
                            Code = "BadGateway",
                            Message = "No mock response found for this request"
                        }
                    }
                };

                Logger.LogRequest($"502 {request.Url}", MessageType.Mocked, args.Request);
            }
            else
            {
                dynamic? body = null;
                var statusCode = HttpStatusCode.OK;
                if (mockResponse.Response?.StatusCode is not null)
                {
                    statusCode = (HttpStatusCode)mockResponse.Response.StatusCode;
                }

                if (mockResponse.Response?.Headers is not null)
                {
                    ProxyUtils.MergeHeaders(headers, [.. mockResponse.Response.Headers]);
                }

                // default the content type to application/json unless set in the mock response
                if (!headers.Any(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                {
                    headers.Add(new("content-type", "application/json"));
                }

                if (mockResponse.Response?.Body is not null)
                {
                    var bodyString = JsonSerializer.Serialize(mockResponse.Response.Body, ProxyUtils.JsonSerializerOptions) as string;
                    // we get a JSON string so need to start with the opening quote
                    if (bodyString?.StartsWith("\"@", StringComparison.OrdinalIgnoreCase) ?? false)
                    {
                        // we've got a mock body starting with @-token which means we're sending
                        // a response from a file on disk
                        // if we can read the file, we can immediately send the response and
                        // skip the rest of the logic in this method
                        // remove the surrounding quotes and the @-token
                        var filePath = Path.Combine(Path.GetDirectoryName(Configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(bodyString.Trim('"')[1..]));
                        if (!File.Exists(filePath))
                        {
                            Logger.LogError("File {FilePath} not found. Serving file path in the mock response", filePath);
                            body = bodyString;
                        }
                        else
                        {
                            var bodyBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                            body = Convert.ToBase64String(bodyBytes);
                        }
                    }
                    else
                    {
                        body = mockResponse.Response.Body;
                    }
                }
                response = new()
                {
                    Id = request.Id,
                    Status = (int)statusCode,
                    Headers = headers.ToDictionary(h => h.Name, h => h.Value),
                    Body = body
                };

                Logger.LogRequest($"{mockResponse.Response?.StatusCode ?? 200} {mockResponse.Request?.Url}", MessageType.Mocked, args.Request);
            }

            responses.Add(response);
        }

        var batchRequestId = Guid.NewGuid().ToString();
        var batchRequestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var batchHeaders = ProxyUtils.BuildGraphResponseHeaders(args.Request, batchRequestId, batchRequestDate);
        var batchResponse = new GraphBatchResponsePayload
        {
            Responses = [.. responses]
        };
        var batchResponseString = JsonSerializer.Serialize(batchResponse, ProxyUtils.JsonSerializerOptions);
        ProcessMockResponse(ref batchResponseString, batchHeaders, args.Request, null);

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(batchResponseString ?? string.Empty, Encoding.UTF8, "application/json")
        };

        foreach (var header in batchHeaders)
        {
            _ = httpResponse.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        Logger.LogRequest($"200 {args.Request.RequestUri}", MessageType.Mocked, args.Request);
        return PluginResponse.Respond(httpResponse);
    };

    protected MockResponse? GetMatchingMockResponse(GraphBatchRequestPayloadRequest request, Uri batchRequestUri)
    {
        if (Configuration.NoMocks ||
            Configuration.Mocks is null ||
            !Configuration.Mocks.Any())
        {
            return null;
        }

        var mockResponse = Configuration.Mocks.FirstOrDefault(mockResponse =>
        {
            if (mockResponse.Request?.Method != request.Method)
            {
                return false;
            }
            // URLs in batch are relative to Graph version number so we need
            // to make them absolute using the batch request URL
            var absoluteRequestFromBatchUrl = ProxyUtils
                .GetAbsoluteRequestUrlFromBatch(batchRequestUri, request.Url)
                .ToString();
            if (mockResponse.Request.Url == absoluteRequestFromBatchUrl)
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!mockResponse.Request.Url.Contains('*', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            //turn mock URL with wildcard into a regex and match against the request URL
            var mockResponseUrlRegex = Regex.Escape(mockResponse.Request.Url).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(absoluteRequestFromBatchUrl, $"^{mockResponseUrlRegex}$");
        });
        return mockResponse;
    }
}