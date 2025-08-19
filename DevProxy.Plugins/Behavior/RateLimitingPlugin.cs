// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Behavior;

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

public sealed class RateLimitConfiguration
{
    public int CostPerRequest { get; set; } = 2;
    public MockResponseResponse? CustomResponse { get; set; }
    public string CustomResponseFile { get; set; } = "rate-limit-response.json";
    public string HeaderLimit { get; set; } = "RateLimit-Limit";
    public string HeaderRemaining { get; set; } = "RateLimit-Remaining";
    public string HeaderReset { get; set; } = "RateLimit-Reset";
    public string HeaderRetryAfter { get; set; } = "Retry-After";
    public int RateLimit { get; set; } = 120;
    public RateLimitResetFormat ResetFormat { get; set; } = RateLimitResetFormat.SecondsLeft;
    public int ResetTimeWindowSeconds { get; set; } = 60;
    public int WarningThresholdPercent { get; set; } = 80;
    public RateLimitResponseWhenLimitExceeded WhenLimitExceeded { get; set; } = RateLimitResponseWhenLimitExceeded.Throttle;
}

public sealed class RateLimitingPlugin(
    HttpClient httpClient,
    ILogger<RateLimitingPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    BasePlugin<RateLimitConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly IProxyStorage _proxyStorage = proxyStorage;
    private int _resourcesRemaining = -1;
    private DateTime _resetTime = DateTime.MinValue;
    private RateLimitingCustomResponseLoader? _loader;

    public override string Name => nameof(RateLimitingPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        if (Configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Custom)
        {
            Configuration.CustomResponseFile = ProxyUtils.GetFullPath(Configuration.CustomResponseFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<RateLimitingCustomResponseLoader>(e.ServiceProvider, Configuration);
            await _loader.InitFileWatcherAsync(cancellationToken);
        }
    }

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.FromResult(PluginResponse.Continue());
        }

        // set the initial values for the first request
        if (_resetTime == DateTime.MinValue)
        {
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }
        if (_resourcesRemaining == -1)
        {
            _resourcesRemaining = Configuration.RateLimit;
        }

        // see if we passed the reset time window
        if (DateTime.Now > _resetTime)
        {
            _resourcesRemaining = Configuration.RateLimit;
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }

        // subtract the cost of the request
        _resourcesRemaining -= Configuration.CostPerRequest;
        if (_resourcesRemaining < 0)
        {
            _resourcesRemaining = 0;

            Logger.LogRequest($"Exceeded resource limit when calling {args.Request.RequestUri}. Request will be throttled", MessageType.Failed, args.Request);
            if (Configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Throttle)
            {
                // Add throttling info to global data for RetryAfterPlugin coordination
                if (!_proxyStorage.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                {
                    value = new List<ThrottlerInfo>();
                    _proxyStorage.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                }

                var throttledRequests = value as List<ThrottlerInfo>;
                throttledRequests?.Add(new(
                    BuildThrottleKey(args.Request),
                    ShouldThrottle,
                    _resetTime
                ));

                return Task.FromResult(PluginResponse.Respond(BuildThrottleResponse(args.Request)));
            }
            else
            {
                if (Configuration.CustomResponse is not null)
                {
                    var responseCode = (HttpStatusCode)(Configuration.CustomResponse.StatusCode ?? 200);

                    // Add throttling info for TooManyRequests responses
                    if (responseCode == HttpStatusCode.TooManyRequests)
                    {
                        if (!_proxyStorage.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                        {
                            value = new List<ThrottlerInfo>();
                            _proxyStorage.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                        }

                        var throttledRequests = value as List<ThrottlerInfo>;
                        throttledRequests?.Add(new(
                            BuildThrottleKey(args.Request),
                            ShouldThrottle,
                            _resetTime
                        ));
                    }

                    var response = new HttpResponseMessage(responseCode)
                    {
                        Content = new StringContent(
                            Configuration.CustomResponse.Body is not null ?
                                JsonSerializer.Serialize(Configuration.CustomResponse.Body, ProxyUtils.JsonSerializerOptions) :
                                string.Empty,
                            Encoding.UTF8,
                            "application/json")
                    };

                    // Add headers
                    if (Configuration.CustomResponse.Headers is not null)
                    {
                        foreach (var header in Configuration.CustomResponse.Headers)
                        {
                            var headerValue = header.Value;
                            if (header.Name.Equals(Configuration.HeaderRetryAfter, StringComparison.OrdinalIgnoreCase) && headerValue == "@dynamic")
                            {
                                headerValue = ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                            }
                            _ = response.Headers.TryAddWithoutValidation(header.Name, headerValue);
                        }
                    }

                    return Task.FromResult(PluginResponse.Respond(response));
                }
                else
                {
                    Logger.LogRequest($"Custom behavior not set. {Configuration.CustomResponseFile} not found.", MessageType.Failed, args.Request);
                    var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Custom response file not found.", Encoding.UTF8, "text/plain")
                    };
                    return Task.FromResult(PluginResponse.Respond(response));
                }
            }
        }
        else
        {
            Logger.LogRequest($"Resources remaining: {_resourcesRemaining}", MessageType.Skipped, args.Request);
        }

        StoreRateLimitingHeaders(args);
        return Task.FromResult(PluginResponse.Continue());
    };

    public override Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnResponseAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.FromResult<PluginResponse?>(null);
        }

        // Add rate limiting headers to the response if we have them stored
        var requestData = _proxyStorage.GetRequestData(args.RequestId);
        if (requestData.TryGetValue(Name, out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            var response = args.Response;
            foreach (var header in rateLimitingHeaders)
            {
                _ = response.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        Logger.LogTrace("Left {Name}", nameof(OnResponseAsync));
        return Task.FromResult<PluginResponse?>(null);
    };

    private ThrottlingInfo ShouldThrottle(HttpRequestMessage request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new(throttleKeyForRequest == throttlingKey ?
            (int)(_resetTime - DateTime.Now).TotalSeconds : 0,
            Configuration.HeaderRetryAfter);
    }

    private HttpResponseMessage BuildThrottleResponse(HttpRequestMessage request)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;

        // resources exceeded
        if (ProxyUtils.IsGraphRequest(request))
        {
            var requestId = Guid.NewGuid().ToString();
            var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
            headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

            body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                new()
                {
                    Code = new Regex("([A-Z])").Replace(HttpStatusCode.TooManyRequests.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                    Message = BuildApiErrorMessage(request),
                    InnerError = new()
                    {
                        RequestId = requestId,
                        Date = requestDate
                    }
                }),
                ProxyUtils.JsonSerializerOptions
            );
        }

        headers.Add(new(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
        if (request.Headers.TryGetValues("Origin", out var _))
        {
            headers.Add(new("Access-Control-Allow-Origin", "*"));
            headers.Add(new("Access-Control-Expose-Headers", Configuration.HeaderRetryAfter));
        }

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

    private void StoreRateLimitingHeaders(RequestArguments args)
    {
        // add rate limiting headers if reached the threshold percentage
        if (_resourcesRemaining > Configuration.RateLimit - (Configuration.RateLimit * Configuration.WarningThresholdPercent / 100))
        {
            return;
        }

        var headers = new List<MockResponseHeader>();
        var reset = Configuration.ResetFormat == RateLimitResetFormat.SecondsLeft ?
            (_resetTime - DateTime.Now).TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) :  // drop decimals
            new DateTimeOffset(_resetTime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers.AddRange(
        [
            new(Configuration.HeaderLimit, Configuration.RateLimit.ToString(CultureInfo.InvariantCulture)),
            new(Configuration.HeaderRemaining, _resourcesRemaining.ToString(CultureInfo.InvariantCulture)),
            new(Configuration.HeaderReset, reset)
        ]);

        ExposeRateLimitingForCors(headers, args.Request);

        var requestData = _proxyStorage.GetRequestData(args.RequestId);
        requestData.Add(Name, headers);
    }

    private void ExposeRateLimitingForCors(List<MockResponseHeader> headers, HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("Origin", out var _))
        {
            return;
        }

        headers.Add(new("Access-Control-Allow-Origin", "*"));
        headers.Add(new("Access-Control-Expose-Headers", $"{Configuration.HeaderLimit}, {Configuration.HeaderRemaining}, {Configuration.HeaderReset}, {Configuration.HeaderRetryAfter}"));
    }

    private static string BuildApiErrorMessage(HttpRequestMessage r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : string.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage()) : "")}";

    private static string BuildThrottleKey(HttpRequestMessage r)
    {
        if (ProxyUtils.IsGraphRequest(r))
        {
            return GraphUtils.BuildThrottleKey(r);
        }
        else
        {
            return r.RequestUri?.Host ?? string.Empty;
        }
    }
}
