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
using System.Text;
using System.Text.Json;

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
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    BasePlugin<LanguageModelRateLimitConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly IProxyStorage _proxyStorage = proxyStorage;
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

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (args.Request.Method != HttpMethod.Post || args.Request.Content == null)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        var bodyString = await args.Request.Content.ReadAsStringAsync(cancellationToken);
        if (!TryGetOpenAIRequest(bodyString, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
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
            Logger.LogRequest($"Exceeded token limit when calling {args.Request.RequestUri}. Request will be throttled", MessageType.Failed, args.Request);

            if (Configuration.WhenLimitExceeded == TokenLimitResponseWhenExceeded.Throttle)
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

                return PluginResponse.Respond(BuildThrottleResponse(args.Request));
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

                    return PluginResponse.Respond(response);
                }
                else
                {
                    Logger.LogRequest($"Custom behavior not set. {Configuration.CustomResponseFile} not found.", MessageType.Failed, args.Request);
                    var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Custom response file not found.", Encoding.UTF8, "text/plain")
                    };
                    return PluginResponse.Respond(response);
                }
            }
        }
        else
        {
            Logger.LogDebug("Tokens remaining - Prompt: {PromptTokensRemaining}, Completion: {CompletionTokensRemaining}", _promptTokensRemaining, _completionTokensRemaining);
        }

        return PluginResponse.Continue();
    };

    public override Func<ResponseArguments, CancellationToken, Task<PluginResponse?>>? OnResponseAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnResponseAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return null;
        }

        if (args.Request.Method != HttpMethod.Post || args.Request.Content == null)
        {
            Logger.LogDebug("Skipping non-POST request");
            return null;
        }

        var bodyString = await args.Request.Content.ReadAsStringAsync(cancellationToken);
        if (!TryGetOpenAIRequest(bodyString, out var openAiRequest))
        {
            Logger.LogDebug("Skipping non-OpenAI request");
            return null;
        }

        // Read the response body to get token usage
        var httpResponse = args.Response;
        if (httpResponse.Content != null)
        {
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
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

                        Logger.LogRequest($"Consumed {promptTokens} prompt tokens and {completionTokens} completion tokens. Remaining - Prompt: {_promptTokensRemaining}, Completion: {_completionTokensRemaining}", MessageType.Processed, args.Request);
                    }
                }
                catch (JsonException ex)
                {
                    Logger.LogDebug(ex, "Failed to parse OpenAI response for token usage");
                }
            }
        }

        Logger.LogTrace("Left {Name}", nameof(OnResponseAsync));
        return null;
    };

    private bool TryGetOpenAIRequest(string content, out OpenAIRequest? request)
    {
        request = null;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            Logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            if (rawRequest.TryGetProperty("prompt", out _))
            {
                Logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            if (rawRequest.TryGetProperty("messages", out _))
            {
                Logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            Logger.LogDebug("Request is not an OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            Logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }

    private ThrottlingInfo ShouldThrottle(HttpRequestMessage request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new(throttleKeyForRequest == throttlingKey ?
            (int)(_resetTime - DateTime.Now).TotalSeconds : 0,
            Configuration.HeaderRetryAfter);
    }

    private HttpResponseMessage BuildThrottleResponse(HttpRequestMessage request)
    {
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
        var body = JsonSerializer.Serialize(openAiError, ProxyUtils.JsonSerializerOptions);

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        _ = response.Headers.TryAddWithoutValidation(Configuration.HeaderRetryAfter, ((int)(_resetTime - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture));

        if (request.Headers.TryGetValues("Origin", out var _))
        {
            _ = response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
            _ = response.Headers.TryAddWithoutValidation("Access-Control-Expose-Headers", Configuration.HeaderRetryAfter);
        }

        return response;
    }

    private static string BuildThrottleKey(HttpRequestMessage r) => r.RequestUri?.Host ?? string.Empty;
}
