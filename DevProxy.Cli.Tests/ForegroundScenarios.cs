// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using Xunit;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Runs Dev Proxy in the foreground the way scripts and agents do: stdin closed,
/// ports assigned by the OS, and <c>--timeout</c> to shut down when idle.
/// </summary>
public sealed class ForegroundScenarios
{
    private static readonly string[] s_startArgs =
    [
        "--as-system-proxy", "false", "--port", "0", "--api-port", "0",
        "--install-cert", "false", "--no-first-run", "--timeout", "3",
    ];

    [Fact]
    public async Task JsonOutput_StdOutIsJsonLines_AndInactivityTimeoutStopsProxy()
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, [.. s_startArgs, "--output", "json"],
            timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, result.ToString());
        var lines = DevProxyCli.ParseJsonLines(result.StdOutLines, result);
        _ = DevProxyCli.ParseJsonLines(result.StdErrLines, result);
        Assert.DoesNotContain("Hotkeys", result.StdOut, StringComparison.OrdinalIgnoreCase);

        var messages = lines
            .Where(l => l.TryGetProperty("message", out _))
            .Select(l => l.GetProperty("message").GetString() ?? "")
            .ToArray();
        Assert.Contains(messages, m => m.StartsWith(TestSandbox.PluginLoadedMessage, StringComparison.Ordinal));
        AssertListeningOnRealPort(string.Join('\n', messages), result);
        Assert.Contains(messages, m => m.Contains("No requests received in 3 seconds", StringComparison.Ordinal));

        var started = Assert.Single(lines, l => l.GetProperty("type").GetString() == "result");
        var apiUrl = new Uri(started.GetProperty("data").GetProperty("apiUrl").GetString()!);
        Assert.True(apiUrl.Port > 0, result.ToString());
    }

    [Fact]
    public async Task TextOutput_ReportsRealPorts_AndInactivityTimeoutStopsProxy()
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, s_startArgs, timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, result.ToString());
        AssertListeningOnRealPort(result.StdOut, result);
        Assert.Matches(new Regex(@"API listening on http://[^:\s]+:[1-9]\d*"), result.StdOut);
        Assert.Contains("No requests received in 3 seconds", result.StdOut, StringComparison.Ordinal);
    }

    private static void AssertListeningOnRealPort(string output, CliResult result)
    {
        var match = Regex.Match(output, @"listening on 127\.0\.0\.1:(\d+)");
        Assert.True(match.Success, $"Proxy didn't report its listening address.\n{result}");
        Assert.NotEqual("0", match.Groups[1].Value);
    }
}
