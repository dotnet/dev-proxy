// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.LanguageModel;

public enum LanguageModelClient
{
    Ollama,
    OpenAI,
    AzureOpenAI
}

public class LanguageModelConfiguration
{
    internal const string EnvVarPrefix = "@env:";

    // API key used to authenticate with hosted language models.
    // Use @env:VAR_NAME to read the key from an environment variable.
    public string? ApiKey { get; set; }
    public bool CacheResponses { get; set; } = true;
    public LanguageModelClient Client { get; set; } = LanguageModelClient.OpenAI;
    public bool Enabled { get; set; }
    public string Model { get; set; } = "llama3.2";
    // default Ollama URL
    public string? Url { get; set; } = "http://localhost:11434/v1/";

    public override string ToString() =>
        $"{nameof(LanguageModelConfiguration)} {{ Client = {Client}, Enabled = {Enabled}, Model = {Model}, Url = {Url}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "<not set>" : "***")} }}";
}
