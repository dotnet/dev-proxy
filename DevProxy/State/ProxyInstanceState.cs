// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace DevProxy.State;

/// <summary>
/// Represents the state of a running Dev Proxy instance.
/// Stored in the Dev Proxy configuration folder (see <see cref="StateManager.GetConfigFolder"/>).
/// </summary>
internal sealed class ProxyInstanceState
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("logFile")]
    public string LogFile { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("configFile")]
    public string? ConfigFile { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }
}