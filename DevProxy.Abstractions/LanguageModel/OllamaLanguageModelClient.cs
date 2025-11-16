// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net.Http.Json;
using DevProxy.Abstractions.Prompty;
using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions.LanguageModel;

public sealed class OllamaLanguageModelClient(
    HttpClient httpClient,
    LanguageModelConfiguration configuration,
    ILogger<OllamaLanguageModelClient> logger) : BaseLanguageModelClient(configuration, logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly Dictionary<string, OllamaLanguageModelCompletionResponse> _cacheCompletion = [];
    private readonly Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OllamaLanguageModelChatCompletionResponse> _cacheChatCompletion = [];

    protected override async Task<ILanguageModelCompletionResponse?> GenerateCompletionCoreAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken)
    {
        using var scope = Logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (Configuration.CacheResponses && _cacheCompletion.TryGetValue(prompt, out var cachedResponse))
        {
            Logger.LogDebug("Returning cached response for prompt: {Prompt}", prompt);
            return cachedResponse;
        }

        var response = await GenerateCompletionInternalAsync(prompt, options, cancellationToken);
        if (response == null)
        {
            return null;
        }

        if (response.Error is not null)
        {
            Logger.LogError("{Error}", response.Error);
            return null;
        }

        if (Configuration.CacheResponses && response.Response is not null)
        {
            _cacheCompletion[prompt] = response;
        }

        return response;
    }

    protected override async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionCoreAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var scope = Logger.BeginScope(nameof(OllamaLanguageModelClient));

        if (Configuration.CacheResponses && _cacheChatCompletion.TryGetCacheValue(messages, out var cachedResponse))
        {
            Logger.LogDebug("Returning cached response for message: {LastMessage}", messages.Last().Content);
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync(messages, options);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            Logger.LogError("{Error}", response.Error);
            return null;
        }
        else
        {
            if (Configuration.CacheResponses && response.Response is not null)
            {
                _cacheChatCompletion[messages] = response;
            }

            return response;
        }
    }

    protected override IEnumerable<ILanguageModelChatCompletionMessage> ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        return messages.Select(m => new OllamaLanguageModelChatCompletionMessage
        {
            Role = m.Role ?? "user",
            Content = m.Text ?? string.Empty
        });
    }

    protected override async Task<bool> IsEnabledCoreAsync(CancellationToken cancellationToken)
    {
        if (Configuration is null || !Configuration.Enabled)
        {
            return false;
        }

        if (string.IsNullOrEmpty(Configuration.Url))
        {
            Logger.LogError("URL is not set. Language model will be disabled");
            return false;
        }

        if (string.IsNullOrEmpty(Configuration.Model))
        {
            Logger.LogError("Model is not set. Language model will be disabled");
            return false;
        }

        Logger.LogDebug("Checking LM availability at {Url}...", Configuration.Url);

        try
        {
            var testCompletion = await GenerateCompletionInternalAsync("Are you there? Reply with a yes or no.", null, cancellationToken);
            if (testCompletion?.Error is not null)
            {
                Logger.LogError("Error: {Error}", testCompletion.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Couldn't reach language model at {Url}", Configuration.Url);
            return false;
        }
    }

    private async Task<OllamaLanguageModelCompletionResponse?> GenerateCompletionInternalAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken)
    {
        Debug.Assert(Configuration != null, "Configuration is null");

        try
        {
            var url = $"{Configuration.Url?.TrimEnd('/')}/api/generate";
            Logger.LogDebug("Requesting completion. Prompt: {Prompt}", prompt);

            var response = await _httpClient.PostAsJsonAsync(url,
                new
                {
                    prompt,
                    model = Configuration.Model,
                    stream = false,
                    options
                },
                cancellationToken
            );
            Logger.LogDebug("Response status: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                Logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelCompletionResponse>(cancellationToken);
            if (res is null)
            {
                Logger.LogDebug("Response: null");
                return res;
            }

            Logger.LogDebug("Response: {Response}", res.Response);

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to generate completion");
            return null;
        }
    }

    private async Task<OllamaLanguageModelChatCompletionResponse?> GenerateChatCompletionInternalAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options)
    {
        Debug.Assert(Configuration != null, "Configuration is null");

        try
        {
            var url = $"{Configuration.Url?.TrimEnd('/')}/api/chat";
            Logger.LogDebug("Requesting chat completion. Message: {LastMessage}", messages.Last().Content);

            var response = await _httpClient.PostAsJsonAsync(url,
                new
                {
                    messages,
                    model = Configuration.Model,
                    stream = false,
                    options
                }
            );
            Logger.LogDebug("Response: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync();
                Logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OllamaLanguageModelChatCompletionResponse>();
            if (res is null)
            {
                Logger.LogDebug("Response: null");
                return res;
            }

            res.RequestUrl = url;
            return res;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to generate chat completion");
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