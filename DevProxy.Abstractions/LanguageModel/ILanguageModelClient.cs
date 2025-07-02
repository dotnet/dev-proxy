// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.LanguageModel;

public interface ILanguageModelClient
{
    Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(string promptFileName, Dictionary<string, object> parameters, CancellationToken cancellationToken);
    Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options, CancellationToken cancellationToken);
    Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken);
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);
}