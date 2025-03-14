// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace DevProxy.Plugins.MinimalPermissions;

public class CsomTypesDefinition
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("types")]
    public Dictionary<string, string>? Types { get; set; }

    [JsonPropertyName("actions")]
    public Dictionary<string, CsomActionPermissions>? Actions { get; set; }
    [JsonPropertyName("returnTypes")]
    public Dictionary<string, string>? ReturnTypes { get; set; }
}

public class CsomActionPermissions
{
    [JsonPropertyName("delegated")]
    public string[]? Delegated { get; set; }

    [JsonPropertyName("application")]
    public string[]? Application { get; set; }
}