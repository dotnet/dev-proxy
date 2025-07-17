// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using System.Collections.Concurrent;

namespace DevProxy.Proxy;

#pragma warning disable CA1515 // required for the API controller
public interface IProxyState
#pragma warning restore CA1515
{
    Dictionary<string, object> GlobalData { get; }
    bool IsRecording { get; set; }
    ConcurrentQueue<RequestLog> RequestLogs { get; }
}