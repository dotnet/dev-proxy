// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions.LanguageModel;

public static class LanguageModelClientFactory
{
    public static ILanguageModelClient Create(LanguageModelConfiguration? config, ILogger logger)
    {
        return config?.Client switch
        {
            LanguageModelClient.LMStudio => new LMStudioLanguageModelClient(config, logger),
            LanguageModelClient.Ollama => new OllamaLanguageModelClient(config, logger),
            _ => new OllamaLanguageModelClient(config, logger)
        };
    }
}