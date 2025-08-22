// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Behavior;

public sealed class RetryAfterPlugin(
    ILogger<RetryAfterPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BasePlugin(logger, urlsToWatch)
{
    private readonly IProxyStorage _proxyStorage = proxyStorage;
    public static readonly string ThrottledRequestsKey = "ThrottledRequests";

    public override string Name => nameof(RetryAfterPlugin);

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.FromResult(PluginResponse.Continue());
        }

        if (args.Request.Method == HttpMethod.Options)
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, args.Request);
            return Task.FromResult(PluginResponse.Continue());
        }

        var throttleResponse = CheckIfThrottled(args.Request);
        if (throttleResponse != null)
        {
            return Task.FromResult(PluginResponse.Respond(throttleResponse));
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return Task.FromResult(PluginResponse.Continue());
    };

    private HttpResponseMessage? CheckIfThrottled(HttpRequestMessage request)
    {
        if (!_proxyStorage.GlobalData.TryGetValue(ThrottledRequestsKey, out var value))
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, request);
            return null;
        }

        if (value is not List<ThrottlerInfo> throttledRequests)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, request);
            return null;
        }

        var expiredThrottlers = throttledRequests.Where(t => t.ResetTime < DateTime.Now).ToArray();
        foreach (var throttler in expiredThrottlers)
        {
            _ = throttledRequests.Remove(throttler);
        }

        if (throttledRequests.Count == 0)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, request);
            return null;
        }

        foreach (var throttler in throttledRequests)
        {
            var throttleInfo = throttler.ShouldThrottle(request, throttler.ThrottlingKey);
            if (throttleInfo.ThrottleForSeconds > 0)
            {
                var message = $"Calling {request.RequestUri} before waiting for the Retry-After period. Request will be throttled. Throttling on {throttler.ThrottlingKey}.";
                Logger.LogRequest(message, MessageType.Failed, request);

                throttler.ResetTime = DateTime.Now.AddSeconds(throttleInfo.ThrottleForSeconds);
                return BuildThrottleResponse(request, throttleInfo, string.Join(' ', message));
            }
        }

        Logger.LogRequest("Request not throttled", MessageType.Skipped, request);
        return null;
    }

    private static HttpResponseMessage BuildThrottleResponse(HttpRequestMessage request, ThrottlingInfo throttlingInfo, string message)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;

        // override the response body and headers for the error response
        if (ProxyUtils.IsGraphRequest(request))
        {
            var requestId = Guid.NewGuid().ToString();
            var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
            headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

            body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                new()
                {
                    Code = new Regex("([A-Z])").Replace(HttpStatusCode.TooManyRequests.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                    Message = BuildApiErrorMessage(request, message),
                    InnerError = new()
                    {
                        RequestId = requestId,
                        Date = requestDate
                    }
                }),
                ProxyUtils.JsonSerializerOptions
            );
        }
        else
        {
            // ProxyUtils.BuildGraphResponseHeaders already includes CORS headers
            if (request.Headers.TryGetValues("Origin", out var _))
            {
                headers.Add(new("Access-Control-Allow-Origin", "*"));
                headers.Add(new("Access-Control-Expose-Headers", throttlingInfo.RetryAfterHeaderName));
            }
        }

        headers.Add(new(throttlingInfo.RetryAfterHeaderName, throttlingInfo.ThrottleForSeconds.ToString(CultureInfo.InvariantCulture)));

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json")
        };

        foreach (var header in headers)
        {
            _ = response.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return response;
    }

    private static string BuildApiErrorMessage(HttpRequestMessage r, string message) => $"{message} {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage()) : "")}";
}
