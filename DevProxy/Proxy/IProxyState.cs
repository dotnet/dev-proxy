// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy;

interface IProxyState
{
    Dictionary<string, object> GlobalData { get; set; }
    bool IsRecording { get; set; }
    List<RequestLog> RequestLogs { get; set; }
}