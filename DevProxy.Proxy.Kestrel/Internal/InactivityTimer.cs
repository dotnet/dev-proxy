// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Starts counting down on the first <see cref="Reset"/> and invokes the timeout
/// action once no further activity is reported for the configured interval.
/// </summary>
internal sealed class InactivityTimer : IDisposable
{
    private readonly object _lock = new();
    private readonly long _timeoutSeconds;
    private readonly Action _timeoutAction;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private long _lastActivity;
    private bool _started;
    private bool _stopped;

    public InactivityTimer(long timeoutSeconds, Action timeoutAction, TimeProvider? timeProvider = null)
    {
        _timeoutSeconds = timeoutSeconds;
        _timeoutAction = timeoutAction;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(OnElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _started = true;
            _lastActivity = _timeProvider.GetTimestamp();
            _ = _timer.Change(GetTimerInterval(_timeoutSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnElapsed(object? state)
    {
        lock (_lock)
        {
            if (!_started || _stopped)
            {
                return;
            }

            // Change cannot cancel a callback that has already been queued.
            var elapsedSeconds = _timeProvider.GetElapsedTime(_lastActivity).Ticks / (decimal)TimeSpan.TicksPerSecond;
            var remainingSeconds = _timeoutSeconds - elapsedSeconds;
            if (remainingSeconds > 0)
            {
                _ = _timer.Change(GetTimerInterval(remainingSeconds), Timeout.InfiniteTimeSpan);
                return;
            }

            _stopped = true;
            _timeoutAction();
        }
    }

    // System.Threading.Timer accepts at most uint.MaxValue - 1 milliseconds.
    private static TimeSpan GetTimerInterval(decimal remainingSeconds) =>
        TimeSpan.FromMilliseconds((double)Math.Min(remainingSeconds * 1_000, uint.MaxValue - 1));

    public void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
            _timer.Dispose();
        }
    }
}
