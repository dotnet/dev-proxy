// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.LanguageModel;

public abstract class OpenAIRequest
{
    [JsonPropertyName("frequency_penalty")]
    public long FrequencyPenalty { get; set; }
    [JsonPropertyName("max_tokens")]
    public long MaxTokens { get; set; }
    [JsonPropertyName("presence_penalty")]
    public long PresencePenalty { get; set; }
    public object? Stop { get; set; }
    public bool Stream { get; set; }
    public long Temperature { get; set; }
    [JsonPropertyName("top_p")]
    public double TopP { get; set; }
}

public class OpenAICompletionRequest : OpenAIRequest
{
    public string Prompt { get; set; } = string.Empty;
}

public class OpenAIChatCompletionRequest : OpenAIRequest
{
    public OpenAIChatCompletionMessage[] Messages { get; set; } = [];
}

public class OpenAIError
{
    public string? Message { get; set; }
    public string? Type { get; set; }
    public string? Code { get; set; }
    public string? Param { get; set; }
}

public abstract class OpenAIResponse: ILanguageModelCompletionResponse
{
    public long Created { get; set; }
    public OpenAIError? Error { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Object { get; set; } = "text_completion";
    [JsonPropertyName("prompt_filter_results")]
    public OpenAIResponsePromptFilterResult[] PromptFilterResults { get; set; } = [];
    public OpenAIResponseUsage Usage { get; set; } = new();
    public string? RequestUrl { get; set; }

    public string? ErrorMessage => Error?.Message;
    public abstract string? Response { get; }

    public OpenAIResponse ConvertToOpenAIResponse() => this;
}

public abstract class OpenAIResponse<TChoice> : OpenAIResponse
{
    public TChoice[]? Choices { get; set; }
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

public abstract class OpenAIResponseChoice
{
    [JsonPropertyName("content_filter_results")]
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = new();
    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "length";
    public long Index { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; set; }
}

public class OpenAIResponsePromptFilterResult
{
    [JsonPropertyName("content_filter_results")]
    public Dictionary<string, OpenAIResponseContentFilterResult> ContentFilterResults { get; set; } = new();
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
    public override string? Response => Choices is not null && Choices.Length > 0 ? Choices.Last().Text : null;
}

public class OpenAICompletionResponseChoice : OpenAIResponseChoice
{
    public string Text { get; set; } = string.Empty;
}

public class OpenAIChatCompletionMessage: ILanguageModelChatCompletionMessage
{
    public string Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }

        OpenAIChatCompletionMessage m = (OpenAIChatCompletionMessage)obj;
        return Content == m.Content && Role == m.Role;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Role);
    }
}

public class OpenAIChatCompletionResponse : OpenAIResponse<OpenAIChatCompletionResponseChoice>
{
    override public string? Response => Choices is not null && Choices.Length > 0 ? Choices.Last().Message.Content : null;
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
