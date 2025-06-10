// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions.LanguageModel;

public sealed class OllamaLanguageModelClient(
    HttpClient httpClient,
    LanguageModelConfiguration configuration,
    ILogger<OllamaLanguageModelClient> logger) : ILanguageModelClient
{
    private readonly LanguageModelConfiguration _configuration = configuration;
    private readonly ILogger _logger = logger;
    private readonly HttpClient _httpClient = httpClient;
    private readonly Dictionary<string, OllamaLanguageModelCompletionResponse> _cacheCompletion = [];
    private readonly Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OllamaLanguageModelChatCompletionResponse> _cacheChatCompletion = [];
    private bool? _lmAvailable;

    public async Task<bool> IsEnabledAsync()
    {
        if (_lmAvailable.HasValue)
        {
            return _lmAvailable.Value;
        }

        _lmAvailable = await IsEnabledInternalAsync();
        return _lmAvailable.Value;
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options = null)
    {
        using var scope = _logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (_configuration is null)
        {
            return null;
        }

        if (!_lmAvailable.HasValue)
        {
            _logger.LogError("Language model availability is not checked. Call {IsEnabled} first.", nameof(IsEnabledAsync));
            return null;
        }

        if (!_lmAvailable.Value)
        {
            return null;
        }

        if (_configuration.CacheResponses && _cacheCompletion.TryGetValue(prompt, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for prompt: {Prompt}", prompt);
            return cachedResponse;
        }

        var response = await GenerateCompletionInternalAsync(prompt, options);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError("{Error}", response.Error);
            return null;
        }
        else
        {
            if (_configuration.CacheResponses && response.Response is not null)
            {
                _cacheCompletion[prompt] = response;
            }

            return response;
        }
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options = null)
    {
        using var scope = _logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (_configuration is null)
        {
            return null;
        }

        if (!_lmAvailable.HasValue)
        {
            _logger.LogError("Language model availability is not checked. Call {IsEnabled} first.", nameof(IsEnabledAsync));
            return null;
        }

        if (!_lmAvailable.Value)
        {
            return null;
        }

        if (_configuration.CacheResponses && _cacheChatCompletion.TryGetCacheValue(messages, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for message: {LastMessage}", messages.Last().Content);
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync(messages, options);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError("{Error}", response.Error);
            return null;
        }
        else
        {
            if (_configuration.CacheResponses && response.Response is not null)
            {
                _cacheChatCompletion[messages] = response;
            }

            return response;
        }
    }

    private async Task<bool> IsEnabledInternalAsync()
    {
        if (_configuration is null || !_configuration.Enabled)
        {
            return false;
        }

        if (string.IsNullOrEmpty(_configuration.Url))
        {
            _logger.LogError("URL is not set. Language model will be disabled");
            return false;
        }

        if (string.IsNullOrEmpty(_configuration.Model))
        {
            _logger.LogError("Model is not set. Language model will be disabled");
            return false;
        }

        _logger.LogDebug("Checking LM availability at {Url}...", _configuration.Url);

        try
        {
            var testCompletion = await GenerateCompletionInternalAsync("Are you there? Reply with a yes or no.");
            if (testCompletion?.Error is not null)
            {
                _logger.LogError("Error: {Error}", testCompletion.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't reach language model at {Url}", _configuration.Url);
            return false;
        }
    }

    private async Task<OllamaLanguageModelCompletionResponse?> GenerateCompletionInternalAsync(string prompt, CompletionOptions? options = null)
    {
        Debug.Assert(_configuration != null, "Configuration is null");

        try
        {
            var url = $"{_configuration.Url?.TrimEnd('/')}/api/generate";
            _logger.LogDebug("Requesting completion. Prompt: {Prompt}", prompt);

            var response = await _httpClient.PostAsJsonAsync(url,
                new
                {
                    prompt,
                    model = _configuration.Model,
                    stream = false,
                    options
                }
            );
            _logger.LogDebug("Response status: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelCompletionResponse>();
            if (res is null)
            {
                _logger.LogDebug("Response: null");
                return res;
            }

            _logger.LogDebug("Response: {Response}", res.Response);

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate completion");
            return null;
        }
    }

    private async Task<OllamaLanguageModelChatCompletionResponse?> GenerateChatCompletionInternalAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options = null)
    {
        Debug.Assert(_configuration != null, "Configuration is null");

        try
        {
            var url = $"{_configuration.Url?.TrimEnd('/')}/api/chat";
            _logger.LogDebug("Requesting chat completion. Message: {LastMessage}", messages.Last().Content);

            var response = await _httpClient.PostAsJsonAsync(url,
                new
                {
                    messages,
                    model = _configuration.Model,
                    stream = false,
                    options
                }
            );
            _logger.LogDebug("Response: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelChatCompletionResponse>();
            if (res is null)
            {
                _logger.LogDebug("Response: null");
                return res;
            }

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate chat completion");
            return null;
        }
    }
}

internal static class OllamaCacheChatCompletionExtensions
{
    public static IEnumerable<ILanguageModelChatCompletionMessage>? GetKey(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OllamaLanguageModelChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages)
    {
        return cache.Keys.FirstOrDefault(k => k.SequenceEqual(messages));
    }

    public static bool TryGetCacheValue(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OllamaLanguageModelChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages, out OllamaLanguageModelChatCompletionResponse? value)
    {
        var key = cache.GetKey(messages);
        if (key is null)
        {
            value = null;
            return false;
        }

        value = cache[key];
        return true;
    }
}