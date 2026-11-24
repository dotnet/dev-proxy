// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.State;

using Xunit;

namespace DevProxy.Tests;

public class LogsCommandTests
{
    [Fact]
    public void GetLogStartTime_AfterHotReload_UsesProcessStartRatherThanLatestState()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var state = new ProxyInstanceState
        {
            Pid = process.Id,
            StartedAt = DateTimeOffset.Now.AddMinutes(5)
        };

        var logStartTime = LogsCommand.GetLogStartTime(state);

        Assert.Equal(process.StartTime, logStartTime);
        Assert.Empty(Logging.LogSinceFilter.Filter([" info    Original startup"], logStartTime, state.StartedAt.LocalDateTime));
    }

    [Fact]
    public void GetMultipleInstancesNotice_SingleInstance_ReturnsNull()
    {
        var state = new ProxyInstanceState { Pid = 100 };

        Assert.Null(LogsCommand.GetMultipleInstancesNotice(state, [state]));
    }

    [Fact]
    public void GetMultipleInstancesNotice_MultipleInstances_ListsSelectedAndOtherPids()
    {
        var now = DateTimeOffset.UtcNow;
        var systemProxy = new ProxyInstanceState { Pid = 100, AsSystemProxy = true, StartedAt = now.AddMinutes(-10) };
        var other1 = new ProxyInstanceState { Pid = 200, AsSystemProxy = false, StartedAt = now };
        var other2 = new ProxyInstanceState { Pid = 300, AsSystemProxy = false, StartedAt = now.AddMinutes(-5) };
        List<ProxyInstanceState> states = [other1, systemProxy, other2];

        var selected = StateManager.SelectPrimaryState(states);
        Assert.Same(systemProxy, selected);

        var notice = LogsCommand.GetMultipleInstancesNotice(selected!, states);

        Assert.NotNull(notice);
        Assert.Contains("PID 100 (system proxy instance)", notice, StringComparison.Ordinal);
        Assert.Contains("Other instances: 200, 300", notice, StringComparison.Ordinal);
        Assert.Contains("--pid", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMultipleInstancesNotice_NoSystemProxy_SelectsMostRecentlyStarted()
    {
        var now = DateTimeOffset.UtcNow;
        var older = new ProxyInstanceState { Pid = 100, AsSystemProxy = false, StartedAt = now.AddMinutes(-10) };
        var newer = new ProxyInstanceState { Pid = 200, AsSystemProxy = false, StartedAt = now };
        List<ProxyInstanceState> states = [older, newer];

        var selected = StateManager.SelectPrimaryState(states);
        Assert.Same(newer, selected);

        var notice = LogsCommand.GetMultipleInstancesNotice(selected!, states);

        Assert.NotNull(notice);
        Assert.Contains("PID 200 (most recently started instance)", notice, StringComparison.Ordinal);
        Assert.Contains("Other instances: 100.", notice, StringComparison.Ordinal);
    }
}
