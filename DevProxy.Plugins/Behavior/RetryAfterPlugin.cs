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
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Behavior;

public sealed class RetryAfterPlugin(
    ILogger<RetryAfterPlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public static readonly string ThrottledRequestsKey = "ThrottledRequests";

    public override string Name => nameof(RetryAfterPlugin);

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        ThrottleIfNecessary(e);

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    private void ThrottleIfNecessary(ProxyRequestArgs e)
    {
        var request = e.Session.HttpClient.Request;
        if (!e.GlobalData.TryGetValue(ThrottledRequestsKey, out var value))
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new(e.Session));
            return;
        }

        if (value is not List<ThrottlerInfo> throttledRequests)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new(e.Session));
            return;
        }

        var expiredThrottlers = throttledRequests.Where(t => t.ResetTime < DateTime.Now).ToArray();
        foreach (var throttler in expiredThrottlers)
        {
            _ = throttledRequests.Remove(throttler);
        }

        if (throttledRequests.Count == 0)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new(e.Session));
            return;
        }

        foreach (var throttler in throttledRequests)
        {
            var throttleInfo = throttler.ShouldThrottle(request, throttler.ThrottlingKey);
            if (throttleInfo.ThrottleForSeconds > 0)
            {
                var message = $"Calling {request.Url} before waiting for the Retry-After period. Request will be throttled. Throttling on {throttler.ThrottlingKey}.";
                Logger.LogRequest(message, MessageType.Failed, new(e.Session));

                throttler.ResetTime = DateTime.Now.AddSeconds(throttleInfo.ThrottleForSeconds);
                UpdateProxyResponse(e, throttleInfo, string.Join(' ', message));
                return;
            }
        }

        Logger.LogRequest("Request not throttled", MessageType.Skipped, new(e.Session));
    }

    private static void UpdateProxyResponse(ProxyRequestArgs e, ThrottlingInfo throttlingInfo, string message)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;

        // override the response body and headers for the error response
        if (ProxyUtils.IsGraphRequest(request))
        {
            var requestId = Guid.NewGuid().ToString();
            var now = DateTime.Now;
            var requestDateHeader = now.ToString("r", CultureInfo.InvariantCulture);
            var requestDateInnerError = now.ToString("s", CultureInfo.InvariantCulture);
            headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDateHeader));

            body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                new()
                {
                    Code = new Regex("([A-Z])").Replace(HttpStatusCode.TooManyRequests.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                    Message = BuildApiErrorMessage(request, message),
                    InnerError = new()
                    {
                        RequestId = requestId,
                        Date = requestDateInnerError
                    }
                }),
                ProxyUtils.JsonSerializerOptions
            );
        }
        else
        {
            // ProxyUtils.BuildGraphResponseHeaders already includes CORS headers
            if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new("Access-Control-Allow-Origin", "*"));
                headers.Add(new("Access-Control-Expose-Headers", throttlingInfo.RetryAfterHeaderName));
            }
        }

        headers.Add(new(throttlingInfo.RetryAfterHeaderName, throttlingInfo.ThrottleForSeconds.ToString(CultureInfo.InvariantCulture)));

        e.Session.GenericResponse(body ?? string.Empty, HttpStatusCode.TooManyRequests, headers.Select(h => new HttpHeader(h.Name, h.Value)));
        e.ResponseState.HasBeenSet = true;
    }

    private static string BuildApiErrorMessage(Request r, string message) => $"{message} {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage()) : "")}";
}
