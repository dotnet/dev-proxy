// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PromptyCore = Prompty.Core;
using System.Collections.Concurrent;

namespace DevProxy.Abstractions.LanguageModel;

public abstract class BaseLanguageModelClient(ILogger logger) : ILanguageModelClient
{
    private readonly ILogger _logger = logger;
    private readonly ConcurrentDictionary<string, (IEnumerable<ILanguageModelChatCompletionMessage>?, CompletionOptions?)> _promptCache = new();

    public virtual async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(string promptFileName, Dictionary<string, object> parameters)
    {
        ArgumentNullException.ThrowIfNull(promptFileName, nameof(promptFileName));

        if (!promptFileName.EndsWith(".prompty", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Prompt file name '{PromptFileName}' does not end with '.prompty'. Appending the extension.", promptFileName);
            promptFileName += ".prompty";
        }

        var cacheKey = GetPromptCacheKey(promptFileName, parameters);
        var (messages, options) = _promptCache.GetOrAdd(cacheKey, _ =>
            LoadPrompt(promptFileName, parameters));

        if (messages is null || !messages.Any())
        {
            return null;
        }

        return await GenerateChatCompletionAsync(messages, options);
    }

    public virtual Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options = null) => throw new NotImplementedException();

    public virtual Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options = null) => throw new NotImplementedException();

    public virtual Task<bool> IsEnabledAsync() => throw new NotImplementedException();

    protected virtual IEnumerable<ILanguageModelChatCompletionMessage> ConvertMessages(ChatMessage[] messages) => throw new NotImplementedException();

    private (IEnumerable<ILanguageModelChatCompletionMessage>?, CompletionOptions?) LoadPrompt(string promptFileName, Dictionary<string, object> parameters)
    {
        _logger.LogDebug("Prompt file {PromptFileName} not in the cache. Loading...", promptFileName);

        var filePath = Path.Combine(ProxyUtils.AppFolder!, "prompts", promptFileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Prompt file '{filePath}' not found.");
        }

        _logger.LogDebug("Loading prompt file: {FilePath}", filePath);
        var promptContents = File.ReadAllText(filePath);

        var prompty = PromptyCore.Prompty.Load(promptContents, []);
        if (prompty.Prepare(parameters) is not ChatMessage[] promptyMessages ||
            promptyMessages.Length == 0)
        {
            _logger.LogError("No messages found in the prompt file: {FilePath}", filePath);
            return (null, null);
        }

        var messages = ConvertMessages(promptyMessages);

        var options = new CompletionOptions();
        if (prompty.Model?.Options is not null)
        {
            if (prompty.Model.Options.TryGetValue("temperature", out var temperature))
            {
                options.Temperature = temperature as double?;
            }
            if (prompty.Model.Options.TryGetValue("top_p", out var topP))
            {
                options.TopP = topP as double?;
            }
        }

        return (messages, options);
    }

    private static string GetPromptCacheKey(string promptFileName, Dictionary<string, object> parameters)
    {
        var parametersString = string.Join(";", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{promptFileName}:{parametersString}";
    }
}