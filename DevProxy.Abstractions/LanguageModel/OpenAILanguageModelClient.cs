// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Prompty;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
            _logger.LogError("Error: {Error}", RedactApiKey(response.ErrorMessage));
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Returning cached response for message: {LastMessage}", messages.Last().Content);
            }
            return cachedResponse;
        }

        var response = await GenerateChatCompletionInternalAsync([.. messages.Select(m => (OpenAIChatCompletionMessage)m)], options, cancellationToken);
        if (response == null)
        {
            return null;
        }
        if (response.Error is not null)
        {
            _logger.LogError("Error: {Error}. Code: {Code}", RedactApiKey(response.Error.Message), response.Error.Code);
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
        return messages.Select(m => new OpenAIChatCompletionMessage
        {
            Role = m.Role ?? "user",
            Content = m.Text ?? string.Empty
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

        if (Configuration.Client == LanguageModelClient.AzureOpenAI && string.IsNullOrEmpty(ApiKey))
        {
            _logger.LogError("API key is not set. Azure OpenAI requires an API key. Language model will be disabled");
            return false;
        }

        _logger.LogDebug("Checking LM availability at {Url}...", Configuration.Url);

        try
        {
            var modelsCheck = await CheckModelsEndpointAsync(cancellationToken);
            if (modelsCheck.HasValue)
            {
                return modelsCheck.Value;
            }

            // Fall back to a completion request when /models isn't supported
            // or doesn't list the configured model
            var testCompletion = await GenerateChatCompletionInternalAsync([new()
            {
                Content = "Are you there? Reply with a yes or no.",
                Role = "user"
            }], null, cancellationToken);
            if (testCompletion is null)
            {
                _logger.LogError("Language model at {Url} didn't return a valid completion. Language model will be disabled", Configuration.Url);
                return false;
            }
            if (testCompletion.ErrorMessage is not null)
            {
                _logger.LogError("Error: {Error}", RedactApiKey(testCompletion.ErrorMessage));
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

    // Returns true/false when the /models endpoint gives a definitive answer,
    // null when availability couldn't be determined this way
    private async Task<bool?> CheckModelsEndpointAsync(CancellationToken cancellationToken)
    {
        var url = $"{Configuration.Url?.TrimEnd('/')}/models";
        _logger.LogDebug("Checking available models at {Url}...", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthenticationHeader(request);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Couldn't reach language model at {Url}", Configuration.Url);
            return false;
        }

        using (response)
        {
            _logger.LogDebug("Response: {Response}", response.StatusCode);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                _logger.LogError("Language model at {Url} rejected the request ({StatusCode}). Check the configured API key", Configuration.Url, (int)response.StatusCode);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Models endpoint returned {StatusCode}. Falling back to a completion request", (int)response.StatusCode);
                return null;
            }

            // Azure OpenAI lists base models rather than deployments, so the
            // configured deployment name can't be matched against the list
            if (Configuration.Client == LanguageModelClient.AzureOpenAI)
            {
                return true;
            }

            try
            {
                using var json = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array &&
                    data.EnumerateArray().Any(m =>
                        m.ValueKind == JsonValueKind.Object &&
                        m.TryGetProperty("id", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        IsConfiguredModel(id.GetString())))
                {
                    return true;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Couldn't parse models response");
                return null;
            }

            _logger.LogDebug("Model {Model} not found in the models list. Falling back to a completion request", Configuration.Model);
            return null;
        }
    }

    private bool IsConfiguredModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return false;
        }

        return string.Equals(modelId, Configuration.Model, StringComparison.Ordinal) ||
            // Ollama lists models with their tag, eg. llama3.2:latest
            string.Equals(modelId, $"{Configuration.Model}:latest", StringComparison.Ordinal);
    }

    private async Task<OpenAIChatCompletionResponse?> GenerateChatCompletionInternalAsync(OpenAIChatCompletionMessage[] messages, CompletionOptions? options, CancellationToken cancellationToken = default)
    {
        Debug.Assert(Configuration != null, "Configuration is null");

        try
        {
            var url = $"{Configuration.Url?.TrimEnd('/')}/chat/completions";
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Requesting chat completion. Message: {LastMessage}", messages.Last().Content);
            }

            var payload = new OpenAIChatCompletionRequest
            {
                Messages = messages,
                Model = Configuration.Model,
                Stream = false,
                Temperature = options?.Temperature
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: ProxyUtils.JsonSerializerOptions)
            };
            AddAuthenticationHeader(request);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            _logger.LogDebug("Response: {Response}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("LM error: {ErrorResponse}", RedactApiKey(errorResponse));
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