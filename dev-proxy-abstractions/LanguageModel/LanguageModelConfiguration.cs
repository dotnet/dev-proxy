// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.LanguageModel;

public enum LanguageModelClient
{
    LMStudio,
    Ollama
}

public class LanguageModelConfiguration
{
    public bool CacheResponses { get; set; } = true;
    public bool Enabled { get; set; } = false;
    public LanguageModelClient Client { get; set; } = LanguageModelClient.Ollama;
    // default Ollama URL
    public string? Model { get; set; } = "llama3.2";
    public string? Url { get; set; } = "http://localhost:11434";
}