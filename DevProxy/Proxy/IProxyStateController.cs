// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy;

#pragma warning disable CA1515 // required for the API controller
public interface IProxyStateController
#pragma warning restore CA1515
{
    IProxyState ProxyState { get; }
    Task StartRecordingAsync(CancellationToken cancellationToken);
    Task StopRecordingAsync(CancellationToken cancellationToken);
    Task MockRequestAsync(CancellationToken cancellationToken);
    void StopProxy();
}