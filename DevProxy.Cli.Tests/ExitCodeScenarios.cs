// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Exit code contract: 0 for success (including --help/--version), 2 for invalid
/// input, 1 for runtime failures.
/// </summary>
public sealed class ExitCodeScenarios
{
    // No state file exists for this PID, so status/stop must report a failure.
    private const string UnknownPid = "2147483647";

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("status", "--help")]
    [InlineData("config", "--help")]
    [InlineData("api", "--help")]
    public async Task HelpAndVersion_ExitZero(params string[] args)
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, args);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.NotEmpty(result.StdOutLines);
        Assert.DoesNotContain(TestSandbox.PluginLoadedMessage, result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--port", "abc")]
    [InlineData("--port", "70000")]
    [InlineData("--api-port", "abc")]
    [InlineData("--not-an-option")]
    [InlineData("status", "--not-an-option")]
    public async Task InvalidInput_ExitsTwo(params string[] args)
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, args);

        Assert.True(result.ExitCode == 2, result.ToString());
        Assert.NotEmpty(result.StdErrLines);
    }

    [Fact]
    public async Task InvalidInput_JsonOutput_KeepsStdOutMachineReadable()
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, "--port", "abc", "--output", "json");

        Assert.True(result.ExitCode == 2, result.ToString());
        _ = DevProxyCli.ParseJsonLines(result.StdOutLines, result);
        Assert.Contains("abc", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachedStart_MissingConfig_JsonOutput_WritesStructuredErrorsToStdErr()
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox,
            "--detach", "--as-system-proxy", "false", "--port", "0", "--api-port", "0",
            "--install-cert", "false", "--no-first-run", "--output", "json",
            "--config-file", "missing.json");

        Assert.True(result.ExitCode == 2, result.ToString());
        Assert.Empty(result.StdOutLines);
        var errors = DevProxyCli.ParseJsonLines(result.StdErrLines, result);
        Assert.Contains(errors, e =>
            e.GetProperty("level").GetString() == "error" &&
            e.GetProperty("message").GetString()!.Contains("missing.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public async Task UnknownInstance_ExitsOne(string command)
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, command, "--pid", UnknownPid);

        Assert.True(result.ExitCode == 1, result.ToString());
    }
}
