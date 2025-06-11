// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;

namespace DevProxy.Proxy;

sealed class ProxyState : IProxyState
{
    public bool IsRecording { get; set; }
    public List<RequestLog> RequestLogs { get; set; } = [];
    public Dictionary<string, object> GlobalData { get; set; } = new() {
        { ProxyUtils.ReportsKey, new Dictionary<string, object>() }
    };
}
