// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;

namespace Microsoft.DevProxy;

public interface IProxyState
{
    Dictionary<string, object> GlobalData { get; }
    bool IsRecording { get; }
    IProxyConfiguration ProxyConfiguration { get; }
    List<RequestLog> RequestLogs { get; }
    Task RaiseMockRequestAsync();
    void StartRecording();
    void StopProxy();
    Task StopRecordingAsync();
}