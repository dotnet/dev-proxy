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

public enum RateLimitResponseWhenLimitExceeded
{
    Throttle,
    Custom
}

public enum RateLimitResetFormat
{
    SecondsLeft,
    UtcEpochSeconds
}

public class RateLimitConfiguration
{
    public string HeaderLimit { get; set; } = "RateLimit-Limit";
    public string HeaderRemaining { get; set; } = "RateLimit-Remaining";
    public string HeaderReset { get; set; } = "RateLimit-Reset";
    public string HeaderRetryAfter { get; set; } = "Retry-After";
    public RateLimitResetFormat ResetFormat { get; set; } = RateLimitResetFormat.SecondsLeft;
    public int CostPerRequest { get; set; } = 2;
    public int ResetTimeWindowSeconds { get; set; } = 60;
    public int WarningThresholdPercent { get; set; } = 80;
    public int RateLimit { get; set; } = 120;
    public RateLimitResponseWhenLimitExceeded WhenLimitExceeded { get; set; } = RateLimitResponseWhenLimitExceeded.Throttle;
    public string CustomResponseFile { get; set; } = "rate-limit-response.json";
    public MockResponseResponse? CustomResponse { get; set; }
}

public class RateLimitingPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(RateLimitingPlugin);
    private readonly RateLimitConfiguration _configuration = new();
    // initial values so that we know when we intercept the
    // first request and can set the initial values
    private int _resourcesRemaining = -1;
    private DateTime _resetTime = DateTime.MinValue;
    private RateLimitingCustomResponseLoader? _loader = null;

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? (int)(_resetTime - DateTime.Now).TotalSeconds : 0, _configuration.HeaderRetryAfter);
    }

    private void ThrottleResponse(ProxyRequestArgs e) => UpdateProxyResponse(e, HttpStatusCode.TooManyRequests);

    private void UpdateProxyResponse(ProxyHttpEventArgsBase e, HttpStatusCode errorStatus)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;
        var response = e.Session.HttpClient.Response;

        // resources exceeded
        if (errorStatus == HttpStatusCode.TooManyRequests)
        {
            if (ProxyUtils.IsGraphRequest(request))
            {
                string requestId = Guid.NewGuid().ToString();
                string requestDate = DateTime.Now.ToString();
                headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

                body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                    new GraphErrorResponseError
                    {
                        Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                        Message = BuildApiErrorMessage(request),
                        InnerError = new GraphErrorResponseInnerError
                        {
                            RequestId = requestId,
                            Date = requestDate
                        }
                    }),
                    ProxyUtils.JsonSerializerOptions
                );
            }

            headers.Add(new(_configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString()));
            if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new("Access-Control-Allow-Origin", "*"));
                headers.Add(new("Access-Control-Expose-Headers", _configuration.HeaderRetryAfter));
            }

            e.Session.GenericResponse(body ?? string.Empty, errorStatus, headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray());
            return;
        }

        if (e.SessionData.TryGetValue(Name, out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
        }

        // add headers to the original API response, avoiding duplicates
        headers.ForEach(h => e.Session.HttpClient.Response.Headers.RemoveHeader(h.Name));
        e.Session.HttpClient.Response.Headers.AddHeaders(headers.Select(h => new HttpHeader(h.Name, h.Value)).ToArray());
    }
    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : String.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";

    private static string BuildThrottleKey(Request r)
    {
        if (ProxyUtils.IsGraphRequest(r))
        {
            return GraphUtils.BuildThrottleKey(r);
        }
        else
        {
            return r.RequestUri.Host;
        }
    }

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        if (_configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Custom)
        {
            _configuration.CustomResponseFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.CustomResponseFile), Path.GetDirectoryName(Context.Configuration.ConfigFile ?? string.Empty) ?? string.Empty);
            _loader = new RateLimitingCustomResponseLoader(Logger, _configuration);
            // load the responses from the configured mocks file
            _loader.InitResponsesWatcher();
        }

        PluginEvents.BeforeRequest += OnRequestAsync;
        PluginEvents.BeforeResponse += OnResponseAsync;
    }

    // add rate limiting headers to the response from the API
    private Task OnResponseAsync(object? sender, ProxyResponseArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        UpdateProxyResponse(e, HttpStatusCode.OK);
        return Task.CompletedTask;
    }

    private Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        var session = e.Session;
        var state = e.ResponseState;
        if (state.HasBeenSet)
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

        // set the initial values for the first request
        if (_resetTime == DateTime.MinValue)
        {
            _resetTime = DateTime.Now.AddSeconds(_configuration.ResetTimeWindowSeconds);
        }
        if (_resourcesRemaining == -1)
        {
            _resourcesRemaining = _configuration.RateLimit;
        }

        // see if we passed the reset time window
        if (DateTime.Now > _resetTime)
        {
            _resourcesRemaining = _configuration.RateLimit;
            _resetTime = DateTime.Now.AddSeconds(_configuration.ResetTimeWindowSeconds);
        }

        // subtract the cost of the request
        _resourcesRemaining -= _configuration.CostPerRequest;
        if (_resourcesRemaining < 0)
        {
            _resourcesRemaining = 0;
            var request = e.Session.HttpClient.Request;

            Logger.LogRequest($"Exceeded resource limit when calling {request.Url}. Request will be throttled", MessageType.Failed, new LoggingContext(e.Session));
            if (_configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Throttle)
            {
                if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out object? value))
                {
                    value = new List<ThrottlerInfo>();
                    e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                }

                var throttledRequests = value as List<ThrottlerInfo>;
                throttledRequests?.Add(new ThrottlerInfo(
                    BuildThrottleKey(request),
                    ShouldThrottle,
                    _resetTime
                ));
                ThrottleResponse(e);
                state.HasBeenSet = true;
            }
            else
            {
                if (_configuration.CustomResponse is not null)
                {
                    var headersList = _configuration.CustomResponse.Headers is not null ?
                        _configuration.CustomResponse.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToList() :
                        [];

                    var retryAfterHeader = headersList.FirstOrDefault(h => h.Name.Equals(_configuration.HeaderRetryAfter, StringComparison.OrdinalIgnoreCase));
                    if (retryAfterHeader is not null && retryAfterHeader.Value == "@dynamic")
                    {
                        headersList.Add(new HttpHeader(_configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString()));
                        headersList.Remove(retryAfterHeader);
                    }

                    var headers = headersList.ToArray();

                    // allow custom throttling response
                    var responseCode = (HttpStatusCode)(_configuration.CustomResponse.StatusCode ?? 200);
                    if (responseCode == HttpStatusCode.TooManyRequests)
                    {
                        if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out object? value))
                        {
                            value = new List<ThrottlerInfo>();
                            e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                        }

                        var throttledRequests = value as List<ThrottlerInfo>;
                        throttledRequests?.Add(new ThrottlerInfo(
                            BuildThrottleKey(request),
                            ShouldThrottle,
                            _resetTime
                        ));
                    }

                    string body = _configuration.CustomResponse.Body is not null ?
                        JsonSerializer.Serialize(_configuration.CustomResponse.Body, ProxyUtils.JsonSerializerOptions) :
                        "";
                    e.Session.GenericResponse(body, responseCode, headers);
                    state.HasBeenSet = true;
                }
                else
                {
                    Logger.LogRequest($"Custom behavior not set. {_configuration.CustomResponseFile} not found.", MessageType.Failed, new LoggingContext(e.Session));
                }
            }
        }
        else
        {
            Logger.LogRequest($"Resources remaining: {_resourcesRemaining}", MessageType.Skipped, new LoggingContext(e.Session));
        }

        StoreRateLimitingHeaders(e);
        return Task.CompletedTask;
    }

    private void StoreRateLimitingHeaders(ProxyRequestArgs e)
    {
        // add rate limiting headers if reached the threshold percentage
        if (_resourcesRemaining > _configuration.RateLimit - (_configuration.RateLimit * _configuration.WarningThresholdPercent / 100))
        {
            return;
        }

        var headers = new List<MockResponseHeader>();
        var reset = _configuration.ResetFormat == RateLimitResetFormat.SecondsLeft ?
            (_resetTime - DateTime.Now).TotalSeconds.ToString("N0") :  // drop decimals
            new DateTimeOffset(_resetTime).ToUnixTimeSeconds().ToString();
        headers.AddRange(
        [
            new(_configuration.HeaderLimit, _configuration.RateLimit.ToString()),
            new(_configuration.HeaderRemaining, _resourcesRemaining.ToString()),
            new(_configuration.HeaderReset, reset)
        ]);

        ExposeRateLimitingForCors(headers, e);

        e.SessionData.Add(Name, headers);
    }

    private void ExposeRateLimitingForCors(List<MockResponseHeader> headers, ProxyRequestArgs e)
    {
        var request = e.Session.HttpClient.Request;
        if (request.Headers.FirstOrDefault((h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is null)
        {
            return;
        }

        headers.Add(new("Access-Control-Allow-Origin", "*"));
        headers.Add(new("Access-Control-Expose-Headers", $"{_configuration.HeaderLimit}, {_configuration.HeaderRemaining}, {_configuration.HeaderReset}, {_configuration.HeaderRetryAfter}"));
    }
}
