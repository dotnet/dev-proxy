// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Abstractions.LanguageModel;

public class OllamaLanguageModelClient(LanguageModelConfiguration? configuration, ILogger logger) : ILanguageModelClient
{
    private readonly LanguageModelConfiguration? _configuration = configuration;
    private readonly ILogger _logger = logger;
    private bool? _lmAvailable;
    private readonly Dictionary<string, OllamaLanguageModelCompletionResponse> _cacheCompletion = [];
    private readonly Dictionary<ILanguageModelChatCompletionMessage[], OllamaLanguageModelChatCompletionResponse> _cacheChatCompletion = [];

    public async Task<bool> IsEnabledAsync()
    {
        if (_lmAvailable.HasValue)
        {
            return _lmAvailable.Value;
        }

        _lmAvailable = await IsEnabledInternalAsync();
        return _lmAvailable.Value;
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
        
        _logger.LogDebug("Checking LM availability at {url}...", _configuration.Url);

        try
        {
            // check if lm is on
            using var client = new HttpClient();
            var response = await client.GetAsync(_configuration.Url);
            _logger.LogDebug("Response: {response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var testCompletion = await GenerateCompletionInternalAsync("Are you there? Reply with a yes or no.");
            if (testCompletion?.Error is not null)
            {
                _logger.LogError("Error: {error}", testCompletion.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't reach language model at {url}", _configuration.Url);
            return false;
        }
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt)
    {
        using var scope = _logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (_configuration is null)
        {
            return null;
        }

        if (!_lmAvailable.HasValue)
        {
            _logger.LogError("Language model availability is not checked. Call {isEnabled} first.", nameof(IsEnabledAsync));
            return null;
        }

        if (!_lmAvailable.Value)
        {
            return null;
        }

        if (_configuration.CacheResponses && _cacheCompletion.TryGetValue(prompt, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for prompt: {prompt}", prompt);
            return cachedResponse;
        }

        var response = await GenerateCompletionInternalAsync(prompt);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError(response.Error);
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

    private async Task<OllamaLanguageModelCompletionResponse?> GenerateCompletionInternalAsync(string prompt)
    {
        Debug.Assert(_configuration != null, "Configuration is null");

        try
        {
            using var client = new HttpClient();
            var url = $"{_configuration.Url}/api/generate";
            _logger.LogDebug("Requesting completion. Prompt: {prompt}", prompt);

            var response = await client.PostAsJsonAsync(url,
                new
                {
                    prompt,
                    model = _configuration.Model,
                    stream = false
                }
            );
            _logger.LogDebug("Response: {response}", response.StatusCode);

            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelCompletionResponse>();
            if (res is null)
            {
                return res;
            }

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate completion");
            return null;
        }
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(ILanguageModelChatCompletionMessage[] messages)
    {
        using var scope = _logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (_configuration is null)
        {
            return null;
        }

        if (!_lmAvailable.HasValue)
        {
            _logger.LogError("Language model availability is not checked. Call {isEnabled} first.", nameof(IsEnabledAsync));
            return null;
        }

        if (!_lmAvailable.Value)
        {
            return null;
        }

        if (_configuration.CacheResponses && _cacheChatCompletion.TryGetValue(messages, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for message: {lastMessage}", messages.Last().Content);
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync(messages);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError(response.Error);
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

    private async Task<OllamaLanguageModelChatCompletionResponse?> GenerateChatCompletionInternalAsync(ILanguageModelChatCompletionMessage[] messages)
    {
        Debug.Assert(_configuration != null, "Configuration is null");

        try
        {
            using var client = new HttpClient();
            var url = $"{_configuration.Url}/api/chat";
            _logger.LogDebug("Requesting chat completion. Message: {lastMessage}", messages.Last().Content);

            var response = await client.PostAsJsonAsync(url,
                new
                {
                    messages,
                    model = _configuration.Model,
                    stream = false
                }
            );
            _logger.LogDebug("Response: {response}", response.StatusCode);
            
            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelChatCompletionResponse>();
            if (res is null)
            {
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

internal static class CacheChatCompletionExtensions
{
    public static OllamaLanguageModelChatCompletionMessage[]? GetKey(
        this Dictionary<OllamaLanguageModelChatCompletionMessage[], OllamaLanguageModelChatCompletionResponse> cache,
        ILanguageModelChatCompletionMessage[] messages)
    {
        return cache.Keys.FirstOrDefault(k => k.SequenceEqual(messages));
    }

    public static bool TryGetValue(
        this Dictionary<OllamaLanguageModelChatCompletionMessage[], OllamaLanguageModelChatCompletionResponse> cache,
        ILanguageModelChatCompletionMessage[] messages, out OllamaLanguageModelChatCompletionResponse? value)
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