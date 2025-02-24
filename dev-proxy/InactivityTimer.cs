// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy;

internal class InactivityTimer(long timeoutSeconds, Action timeoutAction)
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    private readonly Timer _timer = new(_ => timeoutAction.Invoke(), null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);
    public void Reset() => _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
    public void Stop() => _timer.Dispose();
}