// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Abstractions.LanguageModel;

public class LanguageModelConfiguration
{
    public bool Enabled { get; set; } = false;
    // default Ollama URL
    public string? Url { get; set; } = "http://localhost:11434";
    public string? Model { get; set; } = "phi3";
    public bool CacheResponses { get; set; } = true;
}