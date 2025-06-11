// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.LanguageModel;

public interface ILanguageModelCompletionResponse
{
    string? ErrorMessage { get; }
    string? Response { get; }
    // custom property added to log in the mock output
    string? RequestUrl { get; set; }

    OpenAIResponse ConvertToOpenAIResponse();
}