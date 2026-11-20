// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Management subcommands run next to a config that loads plugins. Their output must
/// contain only their own result: no plugin or proxy startup logs.
/// </summary>
public sealed class SubcommandOutputScenarios
{
    [Theory]
    [InlineData("outdated", "--output", "json")]
    [InlineData("api", "show", "--output", "json")]
    [InlineData("config", "validate", "--output", "json")]
    public async Task JsonOutput_IsJsonLinesWithoutStartupLogs(params string[] args)
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, args);

        Assert.True(result.ExitCode == 0, result.ToString());
        var lines = DevProxyCli.ParseJsonLines(result.StdOutLines, result);
        Assert.NotEmpty(lines);
        AssertNoStartupLogs(result);
    }

    [Theory]
    [InlineData("outdated")]
    [InlineData("outdated", "--short")]
    [InlineData("api", "show")]
    [InlineData("config", "validate")]
    public async Task TextOutput_HasNoStartupLogs(params string[] args)
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, args);

        Assert.True(result.ExitCode == 0, result.ToString());
        AssertNoStartupLogs(result);
    }

    [Fact]
    public async Task JwtCreate_PrintsOnlyTheToken()
    {
        await using var sandbox = new TestSandbox();

        var result = await DevProxyCli.RunAsync(sandbox, "jwt", "create", "--name", "Dev Proxy CLI tests");

        Assert.True(result.ExitCode == 0, result.ToString());
        var token = Assert.Single(result.StdOutLines);
        Assert.Matches(@"^[\w-]+\.[\w-]+\.[\w-]+$", token);
        AssertNoStartupLogs(result);
    }

    private static void AssertNoStartupLogs(CliResult result)
    {
        var output = result.StdOut + result.StdErr;
        Assert.False(output.Contains(TestSandbox.PluginLoadedMessage, StringComparison.Ordinal),
            $"Plugin log leaked into subcommand output.\n{result}");
        Assert.False(output.Contains("listening on", StringComparison.OrdinalIgnoreCase),
            $"Proxy startup log leaked into subcommand output.\n{result}");
    }
}
