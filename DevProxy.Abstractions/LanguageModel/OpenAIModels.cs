// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }
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
