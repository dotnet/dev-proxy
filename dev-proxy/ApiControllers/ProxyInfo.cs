// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.ApiControllers;

public class ProxyInfo
{
    public bool? Recording { get; set; }
    public string? ConfigFile { get; init; }

    public static ProxyInfo From(IProxyState proxyState)
    {
        return new ProxyInfo
        {
            ConfigFile = proxyState.ProxyConfiguration.ConfigFile,
            Recording = proxyState.IsRecording
        };
    }
}
