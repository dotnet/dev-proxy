// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Abstractions.LanguageModel;

public abstract class OllamaResponse : ILanguageModelCompletionResponse
{
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.MinValue;
    public bool Done { get; set; } = false;
    public string? Error { get; set; }
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
    // custom property added to log in the mock output
    public string RequestUrl { get; set; } = string.Empty;
}

public class OllamaLanguageModelCompletionResponse : OllamaResponse
{
    public int[] Context { get; set; } = [];
}

public class OllamaLanguageModelChatCompletionResponse : OllamaResponse
{
    public OllamaLanguageModelChatCompletionMessage Message { get; set; } = new();
    public override string? Response
    {
        get => Message.Content;
        set
        {
            if (value is null)
            {
                return;
            }

            Message = new() { Content = value };
        }
    }
}

public class OllamaLanguageModelChatCompletionMessage : ILanguageModelChatCompletionMessage
{
    public string Content { get; set; } = string.Empty;
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