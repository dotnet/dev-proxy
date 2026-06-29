// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;

namespace DevProxy.Integration.Tests;

/// <summary>
/// A language model client that is always disabled and never returns a completion. The
/// spec generators (OpenAPI, TypeSpec) only consult the LM for cosmetic enrichment
/// (operation ids / descriptions) and fall back to deterministic generation when it
/// yields nothing — so this keeps those generators fully hermetic.
/// </summary>
internal sealed class DisabledLanguageModelClient : ILanguageModelClient
{
    public Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(
        string promptFileName, Dictionary<string, object> parameters, CancellationToken cancellationToken) =>
        Task.FromResult<ILanguageModelCompletionResponse?>(null);

    public Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(
        IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options, CancellationToken cancellationToken) =>
        Task.FromResult<ILanguageModelCompletionResponse?>(null);

    public Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(
        string prompt, CompletionOptions? options, CancellationToken cancellationToken) =>
        Task.FromResult<ILanguageModelCompletionResponse?>(null);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
