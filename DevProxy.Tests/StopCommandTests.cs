// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
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

        var exitCode = await CreateRootCommand(new StopCommand(logger, NullLogger<DevProxy.Proxy.SystemProxyManager>.Instance))
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
