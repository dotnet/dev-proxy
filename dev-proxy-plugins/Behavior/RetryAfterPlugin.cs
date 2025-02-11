// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft.DevProxy.Plugins.Behavior;

public class RetryAfterPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(RetryAfterPlugin);
    public static readonly string ThrottledRequestsKey = "ThrottledRequests";

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.BeforeRequest += OnRequestAsync;
    }

    private Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (UrlsToWatch is null ||
            !e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        ThrottleIfNecessary(e);
        return Task.CompletedTask;
    }

    private void ThrottleIfNecessary(ProxyRequestArgs e)
    {
        var request = e.Session.HttpClient.Request;
        if (!e.GlobalData.TryGetValue(ThrottledRequestsKey, out object? value))
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (value is not List<ThrottlerInfo> throttledRequests)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var expiredThrottlers = throttledRequests.Where(t => t.ResetTime < DateTime.Now).ToArray();
        foreach (var throttler in expiredThrottlers)
        {
            throttledRequests.Remove(throttler);
        }

        if (throttledRequests.Count == 0)
        {
            Logger.LogRequest("Request not throttled", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        foreach (var throttler in throttledRequests)
        {
            var throttleInfo = throttler.ShouldThrottle(request, throttler.ThrottlingKey);
            if (throttleInfo.ThrottleForSeconds > 0)
            {
                var message = $"Calling {request.Url} before waiting for the Retry-After period. Request will be throttled. Throttling on {throttler.ThrottlingKey}.";
                Logger.LogRequest(message, MessageType.Failed, new LoggingContext(e.Session));

                throttler.ResetTime = DateTime.Now.AddSeconds(throttleInfo.ThrottleForSeconds);
                UpdateProxyResponse(e, throttleInfo, string.Join(' ', message));
                return;
            }
        }

        Logger.LogRequest("Request not throttled", MessageType.Skipped, new LoggingContext(e.Session));
    }

    private static void UpdateProxyResponse(ProxyRequestArgs e, ThrottlingInfo throttlingInfo, string message)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;

        // override the response body and headers for the error response
        if (ProxyUtils.IsGraphRequest(request))
        {
            string requestId = Guid.NewGuid().ToString();
            string requestDate = DateTime.Now.ToString();
            headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

            body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                new GraphErrorResponseError
                {
                    Code = new Regex("([A-Z])").Replace(HttpStatusCode.TooManyRequests.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                    Message = BuildApiErrorMessage(request, message),
                    InnerError = new GraphErrorResponseInnerError
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
            if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new("Access-Control-Allow-Origin", "*"));
                headers.Add(new("Access-Control-Expose-Headers", throttlingInfo.RetryAfterHeaderName));
            }
        }

        headers.Add(new(throttlingInfo.RetryAfterHeaderName, throttlingInfo.ThrottleForSeconds.ToString()));

        e.Session.GenericResponse(body ?? string.Empty, HttpStatusCode.TooManyRequests, headers.Select(h => new HttpHeader(h.Name, h.Value)));
        e.ResponseState.HasBeenSet = true;
    }

    private static string BuildApiErrorMessage(Request r, string message) => $"{message} {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";
}
