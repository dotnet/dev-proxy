// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.LanguageModel;

public abstract class OllamaResponse : ILanguageModelCompletionResponse
{
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.MinValue;
    public bool Done { get; set; } = false;
    public string? Error { get; set; }
    public string? ErrorMessage => Error;
    [JsonPropertyName("eval_count")]
    public long EvalCount { get; set; }
    [JsonPropertyName("eval_duration")]
    public long EvalDuration { get; set; }
    [JsonPropertyName("load_duration")]
    public long LoadDuration { get; set; }
    public string Model { get; set; } = string.Empty;
    [JsonPropertyName("prompt_eval_count")]
    public long PromptEvalCount { get; set; }
    [JsonPropertyName("prompt_eval_duration")]
    public long PromptEvalDuration { get; set; }
    public virtual string? Response { get; set; }
    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }
    public string? RequestUrl { get; set; }

    public abstract OpenAIResponse ConvertToOpenAIResponse();
}

public class OllamaLanguageModelCompletionResponse : OllamaResponse
{
    public int[] Context { get; set; } = [];

    public override OpenAIResponse ConvertToOpenAIResponse()
    {
        return new OpenAICompletionResponse
        {
            Id = Guid.NewGuid().ToString(),
            Object = "text_completion",
            Created = ((DateTimeOffset)CreatedAt).ToUnixTimeSeconds(),
            Model = Model,
            PromptFilterResults =
            [
                new OpenAIResponsePromptFilterResult
                {
                    PromptIndex = 0,
                    ContentFilterResults = new Dictionary<string, OpenAIResponseContentFilterResult>
                    {
                        { "hate", new() { Filtered = false, Severity = "safe" } },
                        { "self_harm", new() { Filtered = false, Severity = "safe" } },
                        { "sexual", new() { Filtered = false, Severity = "safe" } },
                        { "violence", new() { Filtered = false, Severity = "safe" } }
                    }
                }
            ],
            Choices =
            [
                new OpenAICompletionResponseChoice
                {
                    Text = Response ?? string.Empty,
                    Index = 0,
                    FinishReason = "length",
                    ContentFilterResults = new Dictionary<string, OpenAIResponseContentFilterResult>
                    {
                        { "hate", new() { Filtered = false, Severity = "safe" } },
                        { "self_harm", new() { Filtered = false, Severity = "safe" } },
                        { "sexual", new() { Filtered = false, Severity = "safe" } },
                        { "violence", new() { Filtered = false, Severity = "safe" } }
                    }
                }
            ],
            Usage = new OpenAIResponseUsage
            {
                PromptTokens = PromptEvalCount,
                CompletionTokens = EvalCount,
                TotalTokens = PromptEvalCount + EvalCount
            }
        };
    }
}

public class OllamaLanguageModelChatCompletionResponse : OllamaResponse
{
    public OllamaLanguageModelChatCompletionMessage Message { get; set; } = new();
    public override string? Response
    {
        get => Message.Content.ToString();
        set
        {
            if (value is null)
            {
                return;
            }

            Message = new() { Content = value };
        }
    }

    public override OpenAIResponse ConvertToOpenAIResponse()
    {
        return new OpenAIChatCompletionResponse
        {
            Choices = [new OpenAIChatCompletionResponseChoice
            {
                ContentFilterResults = new Dictionary<string, OpenAIResponseContentFilterResult>
                {
                    { "hate", new() { Filtered = false, Severity = "safe" } },
                    { "self_harm", new() { Filtered = false, Severity = "safe" } },
                    { "sexual", new() { Filtered = false, Severity = "safe" } },
                    { "violence", new() { Filtered = false, Severity = "safe" } }
                },
                FinishReason = "stop",
                Index = 0,
                Message = new()
                {
                    Content = Message.Content.ToString() ?? string.Empty,
                    Role = Message.Role
                }
            }],
            Created = ((DateTimeOffset)CreatedAt).ToUnixTimeSeconds(),
            Id = Guid.NewGuid().ToString(),
            Model = Model,
            Object = "chat.completion",
            PromptFilterResults =
            [
                new OpenAIResponsePromptFilterResult
                {
                    PromptIndex = 0,
                    ContentFilterResults = new Dictionary<string, OpenAIResponseContentFilterResult>
                    {
                        { "hate", new() { Filtered = false, Severity = "safe" } },
                        { "self_harm", new() { Filtered = false, Severity = "safe" } },
                        { "sexual", new() { Filtered = false, Severity = "safe" } },
                        { "violence", new() { Filtered = false, Severity = "safe" } }
                    }
                }
            ],
            Usage = new OpenAIResponseUsage
            {
                PromptTokens = PromptEvalCount,
                CompletionTokens = EvalCount,
                TotalTokens = PromptEvalCount + EvalCount
            }
        };
    }
}

public class OllamaLanguageModelChatCompletionMessage : ILanguageModelChatCompletionMessage
{
    public object Content { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }

        OllamaLanguageModelChatCompletionMessage m = (OllamaLanguageModelChatCompletionMessage)obj;
        return Content == m.Content && Role == m.Role;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Content, Role);
    }
}