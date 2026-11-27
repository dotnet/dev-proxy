// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using System.Text.Json.Serialization;

namespace DevProxy.ApiControllers;

#pragma warning disable CA1515 // required for the API controller
public sealed class ProxyInfo
#pragma warning restore CA1515
{
    public bool? Recording { get; set; }
    public string? ConfigFile { get; init; }
    /// <summary>
    /// Absolute paths of the report files written after recording stopped.
    /// Only returned in response to stopping recording.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Reports { get; set; }

    public static ProxyInfo From(IProxyState proxyState, IProxyConfiguration proxyConfiguration)
    {
        ArgumentNullException.ThrowIfNull(proxyState);
        ArgumentNullException.ThrowIfNull(proxyConfiguration);

        return new ProxyInfo
        {
            ConfigFile = proxyConfiguration.ConfigFile,
            Recording = proxyState.IsRecording
        };
    }
}
