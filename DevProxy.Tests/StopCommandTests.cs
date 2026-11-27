// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
using DevProxy.Proxy;
using DevProxy.State;
using Microsoft.Extensions.Logging.Abstractions;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Xunit;

namespace DevProxy.Tests;

public class StopCommandTests
{
    // A PID that can't belong to a running process, so stop takes the
    // "no instance" path without touching any real Dev Proxy instance.
    private const int UnusedPid = int.MaxValue;

    [Fact]
    public async Task Stop_JsonOutput_NoInstanceWithPid_EmitsStructuredResult()
    {
        var logger = new RecordingLogger<StopCommand>();

        var exitCode = await CreateRootCommand(new StopCommand(logger, NullLogger<SystemProxyManager>.Instance))
            .Parse(["stop", "--pid", UnusedPid.ToString(CultureInfo.InvariantCulture), "--output", "json"])
            .InvokeAsync();

        Assert.Equal(1, exitCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogEvents.StructuredOutput, entry.EventId);
        using var json = JsonDocument.Parse(entry.Message);
        Assert.Equal(UnusedPid, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("notRunning", json.RootElement.GetProperty("status").GetString());
        Assert.Equal($"No running Dev Proxy instance with PID {UnusedPid}.", json.RootElement.GetProperty("message").GetString());
    }

    internal static RootCommand CreateRootCommand(Command command) =>
    [
        new Option<OutputFormat?>(DevProxyCommand.OutputOptionName) { Recursive = true },
        command
    ];
}

[Collection("management-environment")]
public class StopCommandPidReuseTests
{
    [Fact]
    public async Task StopByPid_DeadLegacyRecordSharesPidWithLiveInstance_StopsLiveInstance()
    {
        var legacyPath = StateManager.GetStateFilePath();
        if (File.Exists(legacyPath) || File.Exists(SystemProxySnapshotStore.DefaultPath))
        {
            // Don't touch a real legacy record or system proxy snapshot.
            return;
        }
        var pid = Environment.ProcessId;
        var statePath = StateManager.GetInstanceStateFilePath(pid);
        Assert.False(File.Exists(statePath));
        try
        {
            // A dead system-proxy instance's legacy record, and a live instance (this test
            // process, with an API that doesn't answer) that later got the same PID.
            await StateManager.SaveStateAsync(new() { Pid = pid, ProcessStartTime = DateTimeOffset.UnixEpoch, AsSystemProxy = true });
            File.Move(statePath, legacyPath);
            await StateManager.SaveStateAsync(new()
            {
                Pid = pid,
                ProcessStartTime = StateManager.GetCurrentProcessStartTime(),
                AsSystemProxy = true,
                ApiUrl = "http://127.0.0.1:1",
            });
            var logger = new RecordingLogger<StopCommand>();

            var exitCode = await StopCommandTests.CreateRootCommand(new StopCommand(logger, NullLogger<SystemProxyManager>.Instance))
                .Parse(["stop", "--pid", pid.ToString(CultureInfo.InvariantCulture), "--output", "json"])
                .InvokeAsync();

            var statuses = logger.Entries
                .Where(e => e.EventId == LogEvents.StructuredOutput)
                .Select(e => JsonElement.Parse(e.Message).GetProperty("status").GetString())
                .ToList();
            Assert.Equal(1, exitCode);
            Assert.Equal(["staleRecordRemoved", "failed"], statuses);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(statePath));
        }
        finally
        {
            File.Delete(statePath);
            File.Delete(legacyPath);
        }
    }
}
