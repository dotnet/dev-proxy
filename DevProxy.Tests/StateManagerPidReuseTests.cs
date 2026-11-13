// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using DevProxy.Commands;
using DevProxy.State;
using Xunit;

namespace DevProxy.Tests;

[Collection("management-environment")]
public class StateManagerPidReuseTests
{
    [Fact]
    public void ProcessStartTime_RoundTripsThroughJson()
    {
        var startTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new ProxyInstanceState { ProcessStartTime = startTime });

        Assert.Contains("\"processStartTime\"", json, StringComparison.Ordinal);
        Assert.Equal(startTime, JsonSerializer.Deserialize<ProxyInstanceState>(json)!.ProcessStartTime);
    }

    [Fact]
    public void ProcessStartTime_MissingInLegacyState_IsNull()
    {
        var state = JsonSerializer.Deserialize<ProxyInstanceState>("""{"pid":1234,"apiUrl":"http://127.0.0.1:8897"}""");

        Assert.NotNull(state);
        Assert.Null(state.ProcessStartTime);
    }

    [Fact]
    public void GetCurrentProcessStartTime_MatchesProcessStartTime()
    {
        using var process = Process.GetCurrentProcess();

        var startTime = StateManager.GetCurrentProcessStartTime();

        Assert.NotNull(startTime);
        Assert.Equal(TimeSpan.Zero, startTime.Value.Offset);
        Assert.True((startTime.Value - process.StartTime.ToUniversalTime()).Duration() <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IsInstanceAlive_MatchingStartTime_ReturnsTrue()
    {
        var state = new ProxyInstanceState
        {
            Pid = Environment.ProcessId,
            ProcessStartTime = StateManager.GetCurrentProcessStartTime()
        };

        Assert.True(StateManager.IsInstanceAlive(state));
    }

    [Fact]
    public void IsInstanceAlive_PidReusedByDifferentProcess_ReturnsFalse()
    {
        var state = new ProxyInstanceState
        {
            Pid = Environment.ProcessId,
            ProcessStartTime = StateManager.GetCurrentProcessStartTime()!.Value.AddMinutes(-30)
        };

        Assert.False(StateManager.IsInstanceAlive(state));
    }

    [Fact]
    public void IsInstanceAlive_LegacyStateWithoutStartTime_FallsBackToPidCheck()
    {
        var state = new ProxyInstanceState { Pid = Environment.ProcessId };

        Assert.True(StateManager.IsInstanceAlive(state));
    }

    [Fact]
    public void IsInstanceAlive_ExitedProcess_ReturnsFalse()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        var pid = process.Id;

        Assert.False(StateManager.IsInstanceAlive(new ProxyInstanceState { Pid = pid }));
    }

    [Fact]
    public void IsSameProcess_StartTimeMismatch_ReturnsFalse()
    {
        using var process = Process.GetCurrentProcess();
        var state = new ProxyInstanceState
        {
            Pid = process.Id,
            ProcessStartTime = StateManager.GetCurrentProcessStartTime()!.Value.AddSeconds(5)
        };

        Assert.False(StateManager.IsSameProcess(process, state));
    }

    [Fact]
    public void VerifyProcessIdentity_ReportsMatchMismatchAndNotRecorded()
    {
        using var process = Process.GetCurrentProcess();
        var startTime = StateManager.GetCurrentProcessStartTime()!.Value;

        Assert.Equal(ProcessIdentity.Match,
            StateManager.VerifyProcessIdentity(process, new ProxyInstanceState { Pid = process.Id, ProcessStartTime = startTime }));
        Assert.Equal(ProcessIdentity.Mismatch,
            StateManager.VerifyProcessIdentity(process, new ProxyInstanceState { Pid = process.Id, ProcessStartTime = startTime.AddMinutes(-30) }));
        Assert.Equal(ProcessIdentity.NotRecorded,
            StateManager.VerifyProcessIdentity(process, new ProxyInstanceState { Pid = process.Id }));
    }

    [Fact]
    public async Task DeleteStateAsync_RecordReplacedByNewInstance_KeepsNewRecord()
    {
        const int pid = int.MaxValue - 7;
        var path = StateManager.GetInstanceStateFilePath(pid);
        var stale = new ProxyInstanceState { Pid = pid, ProcessStartTime = DateTimeOffset.UtcNow.AddHours(-1) };
        var replacement = new ProxyInstanceState { Pid = pid, ProcessStartTime = DateTimeOffset.UtcNow };
        try
        {
            await StateManager.SaveStateAsync(replacement);

            await StateManager.DeleteStateAsync(stale);
            Assert.True(File.Exists(path));

            await StateManager.DeleteStateAsync(replacement);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetLogStartTime_UsesRecordedProcessStartTime()
    {
        var recorded = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var state = new ProxyInstanceState
        {
            // Points at a live, unrelated process to simulate PID reuse
            Pid = Environment.ProcessId,
            ProcessStartTime = recorded
        };

        Assert.Equal(recorded, LogsCommand.GetLogStartTime(state));
    }
}
