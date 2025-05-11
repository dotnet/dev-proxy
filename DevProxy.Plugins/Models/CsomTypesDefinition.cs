// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace DevProxy.Plugins.Models;

#pragma warning disable CA2227
public class CsomTypesDefinition
{

    [JsonPropertyName("actions")]
    public Dictionary<string, CsomActionPermissions>? Actions { get; set; }
    [JsonPropertyName("returnTypes")]
    public Dictionary<string, string>? ReturnTypes { get; set; }
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("types")]
    public Dictionary<string, string>? Types { get; set; }
}
#pragma warning restore CA2227

public class CsomActionPermissions
{
    [JsonPropertyName("application")]
    public IEnumerable<string>? Application { get; set; }
    [JsonPropertyName("delegated")]
    public IEnumerable<string>? Delegated { get; set; }

}