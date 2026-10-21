// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Invokes <paramref name="timeoutAction"/> once no activity has been reported via
/// <see cref="Reset"/> for <paramref name="timeoutSeconds"/> seconds.
/// </summary>
internal sealed class InactivityTimer(long timeoutSeconds, Action timeoutAction) : IDisposable
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    private readonly Timer _timer = new(_ => timeoutAction.Invoke(), null, TimeSpan.FromSeconds(timeoutSeconds), Timeout.InfiniteTimeSpan);

    public void Reset()
    {
        try
        {
            _ = _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // A request raced with shutdown; nothing left to reset.
        }
    }

    public void Dispose() => _timer.Dispose();
}
