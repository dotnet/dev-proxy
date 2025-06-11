// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

interface IProxyStateController
{
    IProxyState ProxyState { get; }
    void StartRecording();
    Task StopRecordingAsync();
    Task MockRequestAsync();
    void StopProxy();
}