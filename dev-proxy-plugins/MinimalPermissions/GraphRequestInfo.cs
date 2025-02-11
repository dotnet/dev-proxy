// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

public class GraphRequestInfo
{
    [JsonPropertyName("requestUrl")]
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
}