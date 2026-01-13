// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Behavior;

public enum TokenLimitResponseWhenExceeded
{
    Throttle,
    Custom
}

public sealed class LanguageModelRateLimitConfiguration
{
    public MockResponseResponse? CustomResponse { get; set; }
    public string CustomResponseFile { get; set; } = "token-limit-response.json";
    public string HeaderRetryAfter { get; set; } = "retry-after";
    public int ResetTimeWindowSeconds { get; set; } = 60; // 1 minute
    public int PromptTokenLimit { get; set; } = 5000;
    public int CompletionTokenLimit { get; set; } = 5000;
    public TokenLimitResponseWhenExceeded WhenLimitExceeded { get; set; } = TokenLimitResponseWhenExceeded.Throttle;
}

public sealed class LanguageModelRateLimitingPlugin(
    HttpClient httpClient,
    ILogger<LanguageModelRateLimitingPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<LanguageModelRateLimitConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    // initial values so that we know when we intercept the
    // first request and can set the initial values
    private int _promptTokensRemaining = -1;
    private int _completionTokensRemaining = -1;
    private DateTime _resetTime = DateTime.MinValue;
    private LanguageModelRateLimitingCustomResponseLoader? _loader;

    public override string Name => nameof(LanguageModelRateLimitingPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        if (Configuration.WhenLimitExceeded == TokenLimitResponseWhenExceeded.Custom)
        {
            Configuration.CustomResponseFile = ProxyUtils.GetFullPath(Configuration.CustomResponseFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<LanguageModelRateLimitingCustomResponseLoader>(e.ServiceProvider, Configuration);
            await _loader.InitFileWatcherAsync(cancellationToken);
        }
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        var session = e.Session;
        var state = e.ResponseState;
        if (state.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (!OpenAIRequest.TryGetCompletionLikeRequest(request.BodyString, Logger, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        // set the initial values for the first request
        if (_resetTime == DateTime.MinValue)
        {
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }
        if (_promptTokensRemaining == -1)
        {
            _promptTokensRemaining = Configuration.PromptTokenLimit;
            _completionTokensRemaining = Configuration.CompletionTokenLimit;
        }

        // see if we passed the reset time window
        if (DateTime.Now > _resetTime)
        {
            _promptTokensRemaining = Configuration.PromptTokenLimit;
            _completionTokensRemaining = Configuration.CompletionTokenLimit;
            _resetTime = DateTime.Now.AddSeconds(Configuration.ResetTimeWindowSeconds);
        }

        // check if we have tokens available
        if (_promptTokensRemaining <= 0 || _completionTokensRemaining <= 0)
        {
            Logger.LogRequest($"Exceeded token limit when calling {request.Url}. Request will be throttled", MessageType.Failed, new LoggingContext(e.Session));

            if (Configuration.WhenLimitExceeded == TokenLimitResponseWhenExceeded.Throttle)
            {
                if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                {
                    value = new List<ThrottlerInfo>();
                    e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                }

                var throttledRequests = value as List<ThrottlerInfo>;
                throttledRequests?.Add(new(
                    BuildThrottleKey(request),
                    ShouldThrottle,
                    _resetTime
                ));
                ThrottleResponse(e);
                state.HasBeenSet = true;
            }
            else
            {
                if (Configuration.CustomResponse is not null)
                {
                    var headersList = Configuration.CustomResponse.Headers is not null ?
                        Configuration.CustomResponse.Headers.Select(h => new HttpHeader(h.Name, h.Value)).ToList() :
                        [];

                    var retryAfterHeader = headersList.FirstOrDefault(h => h.Name.Equals(Configuration.HeaderRetryAfter, StringComparison.OrdinalIgnoreCase));
                    if (retryAfterHeader is not null && retryAfterHeader.Value == "@dynamic")
                    {
                        headersList.Add(new(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
                        _ = headersList.Remove(retryAfterHeader);
                    }

                    var headers = headersList.ToArray();

                    // allow custom throttling response
                    var responseCode = (HttpStatusCode)(Configuration.CustomResponse.StatusCode ?? 200);
                    if (responseCode == HttpStatusCode.TooManyRequests)
                    {
                        if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
                        {
                            value = new List<ThrottlerInfo>();
                            e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
                        }

                        var throttledRequests = value as List<ThrottlerInfo>;
                        throttledRequests?.Add(new(
                            BuildThrottleKey(request),
                            ShouldThrottle,
                            _resetTime
                        ));
                    }

                    string body = Configuration.CustomResponse.Body is not null ?
                        JsonSerializer.Serialize(Configuration.CustomResponse.Body, ProxyUtils.JsonSerializerOptions) :
                        "";
                    e.Session.GenericResponse(body, responseCode, headers);
                    state.HasBeenSet = true;
                }
                else
                {
                    Logger.LogRequest($"Custom behavior not set. {Configuration.CustomResponseFile} not found.", MessageType.Failed, new LoggingContext(e.Session));
                    e.Session.GenericResponse("Custom response file not found.", HttpStatusCode.InternalServerError, []);
                    state.HasBeenSet = true;
                }
            }
        }
        else
        {
            Logger.LogDebug("Tokens remaining - Prompt: {PromptTokensRemaining}, Completion: {CompletionTokensRemaining}", _promptTokensRemaining, _completionTokensRemaining);
        }

        return Task.CompletedTask;
    }

    public override Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogDebug("Skipping non-POST request");
            return Task.CompletedTask;
        }

        if (!OpenAIRequest.TryGetCompletionLikeRequest(request.BodyString, Logger, out var openAiRequest))
        {
            Logger.LogDebug("Skipping non-OpenAI request");
            return Task.CompletedTask;
        }

        // Read the response body to get token usage
        var response = e.Session.HttpClient.Response;
        if (response.HasBody)
        {
            var responseBody = response.BodyString;
            if (!string.IsNullOrEmpty(responseBody))
            {
                try
                {
                    var openAiResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
                    if (openAiResponse?.Usage != null)
                    {
                        var promptTokens = (int)openAiResponse.Usage.PromptTokens;
                        var completionTokens = (int)openAiResponse.Usage.CompletionTokens;

                        _promptTokensRemaining -= promptTokens;
                        _completionTokensRemaining -= completionTokens;

                        if (_promptTokensRemaining < 0)
                        {
                            _promptTokensRemaining = 0;
                        }
                        if (_completionTokensRemaining < 0)
                        {
                            _completionTokensRemaining = 0;
                        }

                        Logger.LogRequest($"Consumed {promptTokens} prompt tokens and {completionTokens} completion tokens. Remaining - Prompt: {_promptTokensRemaining}, Completion: {_completionTokensRemaining}", MessageType.Processed, new LoggingContext(e.Session));
                    }
                }
                catch (JsonException ex)
                {
                    Logger.LogDebug(ex, "Failed to parse OpenAI response for token usage");
                }
            }
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeResponseAsync));
        return Task.CompletedTask;
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new(throttleKeyForRequest == throttlingKey ?
            (int)(_resetTime - DateTime.Now).TotalSeconds : 0,
            Configuration.HeaderRetryAfter);
    }

    private void ThrottleResponse(ProxyRequestArgs e)
    {
        var headers = new List<MockResponseHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;

        // Build standard OpenAI error response for token limit exceeded
        var openAiError = new
        {
            error = new
            {
                message = "You exceeded your current quota, please check your plan and billing details.",
                type = "insufficient_quota",
                param = (object?)null,
                code = "insufficient_quota"
            }
        };
        body = JsonSerializer.Serialize(openAiError, ProxyUtils.JsonSerializerOptions);

        headers.Add(new(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
        if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("Access-Control-Allow-Origin", "*"));
            headers.Add(new("Access-Control-Expose-Headers", Configuration.HeaderRetryAfter));
        }

        e.Session.GenericResponse(body, HttpStatusCode.TooManyRequests, [.. headers.Select(h => new HttpHeader(h.Name, h.Value))]);
    }

    private static string BuildThrottleKey(Request r) => r.RequestUri.Host;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }
}
