// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prompty.Core;

namespace DevProxy.Abstractions.LanguageModel;

public static class LanguageModelClientFactory
{
    public static ILanguageModelClient Create(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        var lmSection = configuration.GetSection("LanguageModel");
        var config = lmSection?.Get<LanguageModelConfiguration>() ?? new();

        InvokerFactory.AutoDiscovery();

        return config.Client switch
        {
            LanguageModelClient.Ollama => ActivatorUtilities.CreateInstance<OllamaLanguageModelClient>(serviceProvider, config),
            LanguageModelClient.OpenAI => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider, config),
            _ => ActivatorUtilities.CreateInstance<OpenAILanguageModelClient>(serviceProvider, config)
        };
    }
}