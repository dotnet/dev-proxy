// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.LanguageModel;

public class OpenAIRequest
{
    [JsonPropertyName("frequency_penalty")]
    public long? FrequencyPenalty { get; set; }
    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; set; }
    public string Model { get; set; } = string.Empty;
    [JsonPropertyName("presence_penalty")]
    public long? PresencePenalty { get; set; }
    public object? Stop { get; set; }
    public bool? Stream { get; set; }
    public double? Temperature { get; set; }
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    public static bool TryGetOpenAIRequest(string content, ILogger logger, out OpenAIRequest? request)
    {
        logger.LogTrace("{Method} called", nameof(TryGetOpenAIRequest));

        request = null;

        if (string.IsNullOrEmpty(content))
        {
            logger.LogDebug("Request content is empty or null");
            return false;
        }

        try
        {
            logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Check for completion request (has "prompt", but not specific to image)
            if (rawRequest.TryGetProperty("prompt", out _) &&
                !rawRequest.TryGetProperty("size", out _) &&
                !rawRequest.TryGetProperty("n", out _))
            {
                logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Chat completion request
            if (rawRequest.TryGetProperty("messages", out _))
            {
                logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Responses API request - has "input" array with objects containing role/content
            // Must be checked before embedding request because both have "input"
            if (IsResponsesApiRequest(rawRequest))
            {
                logger.LogDebug("Request is a Responses API request");
                request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Embedding request
            if (rawRequest.TryGetProperty("input", out _) &&
                rawRequest.TryGetProperty("model", out _) &&
                !rawRequest.TryGetProperty("voice", out _))
            {
                logger.LogDebug("Request is an embedding request");
                request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Image generation request
            if (rawRequest.TryGetProperty("prompt", out _) &&
                (rawRequest.TryGetProperty("size", out _) || rawRequest.TryGetProperty("n", out _)))
            {
                logger.LogDebug("Request is an image generation request");
                request = JsonSerializer.Deserialize<OpenAIImageRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio transcription request
            if (rawRequest.TryGetProperty("file", out _))
            {
                logger.LogDebug("Request is an audio transcription request");
                request = JsonSerializer.Deserialize<OpenAIAudioRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio speech synthesis request
            if (rawRequest.TryGetProperty("input", out _) && rawRequest.TryGetProperty("voice", out _))
            {
                logger.LogDebug("Request is an audio speech synthesis request");
                request = JsonSerializer.Deserialize<OpenAIAudioSpeechRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Fine-tuning request
            if (rawRequest.TryGetProperty("training_file", out _))
            {
                logger.LogDebug("Request is a fine-tuning request");
                request = JsonSerializer.Deserialize<OpenAIFineTuneRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            logger.LogDebug("Request is not an OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }

    /// <summary>
    /// Tries to parse text generation OpenAI requests (completion, chat completion, and responses API).
    /// Used by plugins that only need to handle text-based generation requests, as opposed to
    /// embeddings, audio, images, or fine-tuning requests.
    /// </summary>
    public static bool TryGetCompletionLikeRequest(string content, ILogger logger, out OpenAIRequest? request)
    {
        logger.LogTrace("{Method} called", nameof(TryGetCompletionLikeRequest));

        request = null;

        if (string.IsNullOrEmpty(content))
        {
            logger.LogDebug("Request content is empty or null");
            return false;
        }

        try
        {
            logger.LogDebug("Checking if the request is a completion-like OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Completion request
            if (rawRequest.TryGetProperty("prompt", out _))
            {
                logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Chat completion request
            if (rawRequest.TryGetProperty("messages", out _))
            {
                logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Responses API request - has "input" array with objects containing role/content
            if (IsResponsesApiRequest(rawRequest))
            {
                logger.LogDebug("Request is a Responses API request");
                request = JsonSerializer.Deserialize<OpenAIResponsesRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            logger.LogDebug("Request is not a completion-like OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }

    private static bool IsResponsesApiRequest(JsonElement rawRequest)
    {
        return rawRequest.TryGetProperty("input", out var inputProperty) &&
            inputProperty.ValueKind == JsonValueKind.Array &&
            inputProperty.GetArrayLength() > 0 &&
            inputProperty.EnumerateArray().First().ValueKind == JsonValueKind.Object &&
            (inputProperty.EnumerateArray().First().TryGetProperty("role", out _) ||
             inputProperty.EnumerateArray().First().TryGetProperty("type", out _));
    }
}

public class OpenAIResponse : ILanguageModelCompletionResponse
{
    public long Created { get; set; }
    public OpenAIError? Error { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
#pragma warning disable CA1720
    public string Object { get; set; } = "text_completion";
#pragma warning restore CA1720
    [JsonPropertyName("prompt_filter_results")]
    public IEnumerable<OpenAIResponsePromptFilterResult> PromptFilterResults { get; set; } = [];
    public OpenAIResponseUsage Usage { get; set; } = new();
    public string? RequestUrl { get; set; }

    public string? ErrorMessage => Error?.Message;
    public virtual string? Response { get; }

    public OpenAIResponse ConvertToOpenAIResponse() => this;
}

public class OpenAIResponse<TChoice> : OpenAIResponse
{
    public IEnumerable<TChoice>? Choices { get; set; }
}

public abstract class OpenAIResponseChoice
{
    [JsonPropertyName("content_filter_results")]
#pragma warning disable CA2227
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = [];
#pragma warning restore CA2227
    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";
    public long Index { get; set; }
    [JsonPropertyName("logprobs")]
    public int? LogProbabilities { get; set; }
}

public class OpenAICompletionRequest : OpenAIRequest
{
    public string Prompt { get; set; } = string.Empty;
}

public class OpenAIChatCompletionRequest : OpenAIRequest
{
    public IEnumerable<OpenAIChatCompletionMessage> Messages { get; set; } = [];
}

public class OpenAIError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public class OpenAIResponseUsage
{
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }
    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokenDetails? PromptTokensDetails { get; set; }
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    // Responses API uses different property names (input_tokens, output_tokens)
    // These property aliases allow the same class to deserialize both formats.
    // When JSON contains "input_tokens", it maps to PromptTokens.
    // When JSON contains "output_tokens", it maps to CompletionTokens.
    [JsonPropertyName("input_tokens")]
    public long InputTokens
    {
        get => PromptTokens;
        set => PromptTokens = value;
    }
    [JsonPropertyName("output_tokens")]
    public long OutputTokens
    {
        get => CompletionTokens;
        set => CompletionTokens = value;
    }
}

public class PromptTokenDetails
{
    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }
}

public class OpenAIResponsePromptFilterResult
{
    [JsonPropertyName("content_filter_results")]
#pragma warning disable CA2227
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = [];
#pragma warning restore CA2227
    [JsonPropertyName("prompt_index")]
    public long PromptIndex { get; set; }
}

public class OpenAIResponseContentFilterResult
{
    public bool Filtered { get; set; }
    public string Severity { get; set; } = "safe";
}

public class OpenAICompletionResponse : OpenAIResponse<OpenAICompletionResponseChoice>
{
    public override string? Response => Choices is not null && Choices.Any() ? Choices.Last().Text : null;
}

public class OpenAICompletionResponseChoice : OpenAIResponseChoice
{
    public string Text { get; set; } = string.Empty;
}

#region content parts

public abstract class OpenAIContentPart
{
    public string? Type { get; set; }
}

public class OpenAITextContentPart : OpenAIContentPart
{
    public string? Text { get; set; }
}

public class OpenAIImageContentPartUrl
{
    public string? Detail { get; set; } = "auto";
    public string? Url { get; set; }
}

public class OpenAIImageContentPart : OpenAIContentPart
{
    [JsonPropertyName("image_url")]
    public OpenAIImageContentPartUrl? Url { get; set; }
}

public class OpenAIAudioContentPartInputAudio
{
    public string? Data { get; set; }
    public string? Format { get; set; }
}

public class OpenAIAudioContentPart : OpenAIContentPart
{
    [JsonPropertyName("input_audio")]
    public OpenAIAudioContentPartInputAudio? InputAudio { get; set; }
}

public class OpenAIFileContentPartFile
{
    [JsonPropertyName("file_data")]
    public string? Data { get; set; }
    [JsonPropertyName("file_id")]
    public string? Id { get; set; }
    [JsonPropertyName("filename")]
    public string? Name { get; set; }
}

public class OpenAIFileContentPart : OpenAIContentPart
{
    public OpenAIFileContentPartFile? File { get; set; }
}

#endregion

public class OpenAIChatCompletionMessage : ILanguageModelChatCompletionMessage
{
    [JsonConverter(typeof(OpenAIContentPartJsonConverter))]
    public object Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }

        var m = (OpenAIChatCompletionMessage)obj;
        return Content == m.Content && Role == m.Role;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Role);
    }
}

public class OpenAIChatCompletionResponse : OpenAIResponse<OpenAIChatCompletionResponseChoice>
{
    public override string? Response => Choices is not null && Choices.Any() ?
        Choices.Last().Message.Content : null;
}

public class OpenAIChatCompletionResponseChoice : OpenAIResponseChoice
{
    public OpenAIChatCompletionResponseChoiceMessage Message { get; set; } = new();
}

public class OpenAIChatCompletionResponseChoiceMessage
{
    public string Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class OpenAIAudioRequest : OpenAIRequest
{
    public string File { get; set; } = string.Empty;
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public string? Prompt { get; set; }
    public string? Language { get; set; }
}

public class OpenAIAudioSpeechRequest : OpenAIRequest
{
    public string Input { get; set; } = string.Empty;
    public string Voice { get; set; } = string.Empty;
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public double? Speed { get; set; }
}

public class OpenAIAudioTranscriptionResponse : OpenAIResponse
{
    public string Text { get; set; } = string.Empty;
    public override string? Response => Text;
}

public class OpenAIEmbeddingRequest : OpenAIRequest
{
    public string? Input { get; set; }
    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }
    public int? Dimensions { get; set; }
}

public class OpenAIEmbeddingResponse : OpenAIResponse
{
    public IEnumerable<OpenAIEmbeddingData>? Data { get; set; }
    public override string? Response => null; // Embeddings don't have a text response
}

public class OpenAIEmbeddingData
{
    public IEnumerable<float>? Embedding { get; set; }
    public int Index { get; set; }
#pragma warning disable CA1720
    public string? Object { get; set; }
#pragma warning restore CA1720
}

public class OpenAIFineTuneRequest : OpenAIRequest
{
    [JsonPropertyName("training_file")]
    public string TrainingFile { get; set; } = string.Empty;
    [JsonPropertyName("validation_file")]
    public string? ValidationFile { get; set; }
    public int? Epochs { get; set; }
    [JsonPropertyName("batch_size")]
    public int? BatchSize { get; set; }
    [JsonPropertyName("learning_rate_multiplier")]
    public double? LearningRateMultiplier { get; set; }
    public string? Suffix { get; set; }
}

public class OpenAIFineTuneResponse : OpenAIResponse
{
    [JsonPropertyName("fine_tuned_model")]
    public string? FineTunedModel { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    [JsonPropertyName("training_file")]
    public string TrainingFile { get; set; } = string.Empty;
    [JsonPropertyName("validation_file")]
    public string? ValidationFile { get; set; }
    [JsonPropertyName("result_files")]
    public IEnumerable<object>? ResultFiles { get; set; }
    public override string? Response => FineTunedModel;
}

public class OpenAIImageRequest : OpenAIRequest
{
    public string Prompt { get; set; } = string.Empty;
    public int? N { get; set; }
    public string? Size { get; set; }
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
    public string? User { get; set; }
    public string? Quality { get; set; }
    public string? Style { get; set; }
}

public class OpenAIImageResponse : OpenAIResponse
{
    public IEnumerable<OpenAIImageData>? Data { get; set; }
    public override string? Response => null; // Image responses don't have a text response
}

public class OpenAIImageData
{
    public string? Url { get; set; }
    [JsonPropertyName("b64_json")]
    public string? Base64Json { get; set; }
    [JsonPropertyName("revised_prompt")]
    public string? RevisedPrompt { get; set; }
}

#region Responses API

public class OpenAIResponsesRequest : OpenAIRequest
{
    public IEnumerable<OpenAIResponsesInputItem>? Input { get; set; }
    public string? Instructions { get; set; }
    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }
    [JsonPropertyName("max_output_tokens")]
    public long? MaxOutputTokens { get; set; }
}

public class OpenAIResponsesInputItem
{
    public string Role { get; set; } = string.Empty;
    [JsonConverter(typeof(OpenAIContentPartJsonConverter))]
    public object Content { get; set; } = string.Empty;
    public string? Type { get; set; }
}

public class OpenAIResponsesResponse : OpenAIResponse
{
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public IEnumerable<OpenAIResponsesOutputItem>? Output { get; set; }
    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    public override string? Response => GetTextFromOutput();

    private string? GetTextFromOutput()
    {
        if (Output is null)
        {
            return null;
        }

        var messageOutput = Output.FirstOrDefault(o =>
            string.Equals(o.Type, "message", StringComparison.OrdinalIgnoreCase));
        if (messageOutput?.Content is null)
        {
            return null;
        }

        var textContent = messageOutput.Content.FirstOrDefault(c =>
            string.Equals(c.Type, "output_text", StringComparison.OrdinalIgnoreCase));
        return textContent?.Text;
    }
}

public class OpenAIResponsesOutputItem
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Role { get; set; }
    public IEnumerable<OpenAIResponsesOutputContent>? Content { get; set; }
    public string? Status { get; set; }
}

public class OpenAIResponsesOutputContent
{
    public string? Type { get; set; }
    public string? Text { get; set; }
}

#endregion
