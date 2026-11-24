// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy.Kestrel.Internal;
using Xunit;

namespace DevProxy.Proxy.Kestrel.Tests;

public sealed class InactivityTimerTests
{
    [Fact]
    public void Constructor_DoesNotStartCountdown()
    {
        var clock = new ManualTimeProvider();
        var expired = false;
        using var timer = new InactivityTimer(2, () => expired = true, clock);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.Timer.DueTime);
        Assert.False(expired);

        timer.Reset();
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Timer.DueTime);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Timer.Fire();
        Assert.True(expired);
    }

    [Fact]
    public void Reset_QueuedCallbackChecksLatestActivity()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        using var timer = new InactivityTimer(2, () => calls++, clock);

        timer.Reset();
        clock.Advance(TimeSpan.FromSeconds(2));
        timer.Reset();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        clock.Timer.Fire();

        Assert.Equal(0, calls);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), clock.Timer.DueTime);

        clock.Advance(TimeSpan.FromMilliseconds(1_500));
        clock.Timer.Fire();
        Assert.Equal(1, calls);

        timer.Reset();
        clock.Timer.Fire();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_ResetAndQueuedCallbackAreIgnored()
    {
        var clock = new ManualTimeProvider();
        var expired = false;
        var timer = new InactivityTimer(2, () => expired = true, clock);

        timer.Reset();
        timer.Dispose();
        clock.Advance(TimeSpan.FromSeconds(2));
        timer.Reset();
        clock.Timer.Fire();

        Assert.False(expired);
        Assert.True(clock.Timer.IsDisposed);
    }

    [Theory]
    [InlineData(4_294_968L)]
    [InlineData(long.MaxValue)]
    public void Reset_LargeTimeoutIsAcceptedBySystemTimer(long timeoutSeconds)
    {
        using var timer = new InactivityTimer(timeoutSeconds, () => Assert.Fail("Timeout fired prematurely."));
        timer.Reset();
    }

    [Fact]
    public void Reset_TimeoutBeyondTimerLimitExpiresAfterFullInterval()
    {
        var clock = new ManualTimeProvider();
        var expired = false;
        using var timer = new InactivityTimer(5_000_000, () => expired = true, clock);
        var maximumInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        timer.Reset();
        Assert.Equal(maximumInterval, clock.Timer.DueTime);
        clock.Advance(maximumInterval);
        clock.Timer.Fire();

        Assert.False(expired);
        var remaining = TimeSpan.FromSeconds(5_000_000) - maximumInterval;
        Assert.Equal(remaining, clock.Timer.DueTime);
        clock.Advance(remaining);
        clock.Timer.Fire();
        Assert.True(expired);
    }

    [Fact]
    public void Reset_MaximumLongTimeoutRearmsWithoutOverflow()
    {
        var clock = new ManualTimeProvider();
        var expired = false;
        using var timer = new InactivityTimer(long.MaxValue, () => expired = true, clock);
        var maximumInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        timer.Reset();
        clock.Advance(maximumInterval);
        clock.Timer.Fire();

        Assert.False(expired);
        Assert.Equal(maximumInterval, clock.Timer.DueTime);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public ManualTimer Timer { get; private set; } = null!;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(callback, state);
            Timer.Change(dueTime, period);
            return Timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan DueTime { get; private set; }
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            DueTime = dueTime;
            return true;
        }

        public void Fire() => callback(state);
        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
