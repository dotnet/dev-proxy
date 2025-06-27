// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;

namespace DevProxy.Abstractions.LanguageModel;

public sealed class OpenAILanguageModelClient(
    HttpClient httpClient,
    LanguageModelConfiguration configuration,
    ILogger<OpenAILanguageModelClient> logger) : BaseLanguageModelClient(configuration, logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger _logger = logger;
    private readonly Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> _cacheChatCompletion = [];

    protected override async Task<ILanguageModelCompletionResponse?> GenerateCompletionCoreAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken)
    {
        var response = await GenerateChatCompletionAsync([new OpenAIChatCompletionMessage() { Content = prompt, Role = "user" }], options, cancellationToken);
        if (response == null)
        {
            return null;
        }
        if (response.ErrorMessage is not null)
        {
            _logger.LogError("Error: {Error}", response.ErrorMessage);
            return null;
        }
        var openAIResponse = (OpenAIChatCompletionResponse)response;

        return new OpenAICompletionResponse
        {
            Choices = openAIResponse.Choices?.Select(c => new OpenAICompletionResponseChoice
            {
                ContentFilterResults = c.ContentFilterResults,
                FinishReason = c.FinishReason,
                Index = c.Index,
                LogProbabilities = c.LogProbabilities,
                Text = c.Message.Content
            }).ToArray(),
            Created = openAIResponse.Created,
            Error = openAIResponse.Error,
            Id = openAIResponse.Id,
            Model = openAIResponse.Model,
            Object = openAIResponse.Object,
            PromptFilterResults = openAIResponse.PromptFilterResults,
            Usage = openAIResponse.Usage,
        };
    }

    protected override async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionCoreAsync(
        IEnumerable<ILanguageModelChatCompletionMessage> messages,
        CompletionOptions? options,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(nameof(OpenAILanguageModelClient));

        if (Configuration.CacheResponses && _cacheChatCompletion.TryGetCacheValue(messages, out var cachedResponse))
        {
            _logger.LogDebug("Returning cached response for message: {LastMessage}", messages.Last().Content);
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync([.. messages.Select(m => (OpenAIChatCompletionMessage)m)], options, cancellationToken);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError("Error: {Error}. Code: {Code}", response.Error.Message, response.Error.Code);
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

    protected override IEnumerable<ILanguageModelChatCompletionMessage> ConvertMessages(ChatMessage[] messages)
    {
        return messages.Select(m => new OpenAIChatCompletionMessage
        {
            Role = m.Role.Value,
            Content = m.Text
        });
    }

    protected override async Task<bool> IsEnabledCoreAsync(CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(nameof(OpenAILanguageModelClient));

        if (Configuration is null || !Configuration.Enabled)
        {
            return false;
        }

        if (string.IsNullOrEmpty(Configuration.Url))
        {
            _logger.LogError("URL is not set. Language model will be disabled");
            return false;
        }

        if (string.IsNullOrEmpty(Configuration.Model))
        {
            _logger.LogError("Model is not set. Language model will be disabled");
            return false;
        }

        _logger.LogDebug("Checking LM availability at {Url}...", Configuration.Url);

        try
        {
            var testCompletion = await GenerateChatCompletionInternalAsync([new()
            {
                Content = "Are you there? Reply with a yes or no.",
                Role = "user"
            }], null, cancellationToken);
            if (testCompletion?.ErrorMessage is not null)
            {
                _logger.LogError("Error: {Error}", testCompletion.ErrorMessage);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't reach language model at {Url}", Configuration.Url);
            return false;
        }
    }

    private async Task<OpenAIChatCompletionResponse?> GenerateChatCompletionInternalAsync(OpenAIChatCompletionMessage[] messages, CompletionOptions? options, CancellationToken cancellationToken = default)
    {
        Debug.Assert(Configuration != null, "Configuration is null");

        try
        {
            var url = $"{Configuration.Url?.TrimEnd('/')}/chat/completions";
            _logger.LogDebug("Requesting chat completion. Message: {LastMessage}", messages.Last().Content);

            var payload = new OpenAIChatCompletionRequest
            {
                Messages = messages,
                Model = Configuration.Model,
                Stream = false,
                Temperature = options?.Temperature
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload, ProxyUtils.JsonSerializerOptions, cancellationToken);
            _logger.LogDebug("Response: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("LM error: {ErrorResponse}", errorResponse);
                return null;
            }

            var res = await response.Content.ReadFromJsonAsync<OpenAIChatCompletionResponse>(cancellationToken);
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

internal static class OpenAICacheChatCompletionExtensions
{
    public static IEnumerable<ILanguageModelChatCompletionMessage>? GetKey(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages)
    {
        return cache.Keys.FirstOrDefault(k => k.SequenceEqual(messages));
    }

    public static bool TryGetCacheValue(
        this Dictionary<IEnumerable<ILanguageModelChatCompletionMessage>, OpenAIChatCompletionResponse> cache,
        IEnumerable<ILanguageModelChatCompletionMessage> messages, out OpenAIChatCompletionResponse? value)
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