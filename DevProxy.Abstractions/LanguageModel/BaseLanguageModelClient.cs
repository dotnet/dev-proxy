// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PromptyCore = Prompty.Core;
using System.Collections.Concurrent;

namespace DevProxy.Abstractions.LanguageModel;

public abstract class BaseLanguageModelClient(LanguageModelConfiguration configuration, ILogger logger) : ILanguageModelClient
{
    protected LanguageModelConfiguration Configuration { get; } = configuration;
    protected ILogger Logger { get; } = logger;

    private bool? _lmAvailable;

    private readonly ConcurrentDictionary<string, (IEnumerable<ILanguageModelChatCompletionMessage>?, CompletionOptions?)> _promptCache = new();

    public virtual async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(string promptFileName, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptFileName, nameof(promptFileName));

        if (!promptFileName.EndsWith(".prompty", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Prompt file name '{PromptFileName}' does not end with '.prompty'. Appending the extension.", promptFileName);
            promptFileName += ".prompty";
        }

        var cacheKey = GetPromptCacheKey(promptFileName, parameters);
        var (messages, options) = _promptCache.GetOrAdd(cacheKey, _ =>
            LoadPrompt(promptFileName, parameters));

        if (messages is null || !messages.Any())
        {
            return null;
        }

        return await GenerateChatCompletionAsync(messages, options, cancellationToken);
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options, CancellationToken cancellationToken)
    {
        if (Configuration is null)
        {
            return null;
        }

        if (!await IsEnabledAsync(cancellationToken))
        {
            Logger.LogDebug("Language model is not available.");
            return null;
        }

        return await GenerateChatCompletionCoreAsync(messages, options, cancellationToken);
    }

    public async Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken)
    {
        if (Configuration is null)
        {
            return null;
        }

        if (!await IsEnabledAsync(cancellationToken))
        {
            Logger.LogDebug("Language model is not available.");
            return null;
        }

        return await GenerateCompletionCoreAsync(prompt, options, cancellationToken);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        if (_lmAvailable.HasValue)
        {
            return _lmAvailable.Value;
        }

        _lmAvailable = await IsEnabledCoreAsync(cancellationToken);
        return _lmAvailable.Value;
    }

    protected abstract IEnumerable<ILanguageModelChatCompletionMessage> ConvertMessages(ChatMessage[] messages);

    protected abstract Task<ILanguageModelCompletionResponse?> GenerateChatCompletionCoreAsync(IEnumerable<ILanguageModelChatCompletionMessage> messages, CompletionOptions? options, CancellationToken cancellationToken);

    protected abstract Task<ILanguageModelCompletionResponse?> GenerateCompletionCoreAsync(string prompt, CompletionOptions? options, CancellationToken cancellationToken);

    protected abstract Task<bool> IsEnabledCoreAsync(CancellationToken cancellationToken);

    private (IEnumerable<ILanguageModelChatCompletionMessage>?, CompletionOptions?) LoadPrompt(string promptFileName, Dictionary<string, object> parameters)
    {
        Logger.LogDebug("Prompt file {PromptFileName} not in the cache. Loading...", promptFileName);

        var filePath = Path.Combine(ProxyUtils.AppFolder!, "prompts", promptFileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Prompt file '{filePath}' not found.");
        }

        Logger.LogDebug("Loading prompt file: {FilePath}", filePath);
        var promptContents = File.ReadAllText(filePath);

        var prompty = PromptyCore.Prompty.Load(promptContents, []);
        if (prompty.Prepare(parameters) is not ChatMessage[] promptyMessages ||
            promptyMessages.Length == 0)
        {
            Logger.LogError("No messages found in the prompt file: {FilePath}", filePath);
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