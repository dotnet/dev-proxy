// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Xunit;

namespace DevProxy.Tests;

[CollectionDefinition("console-output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection;

[Collection("console-output")]
public sealed class PluginCommandHelpTests
{
    [Theory]
    [InlineData(new[] { "plugin", "--help" }, "https://go.devproxy.net?go=cli")]
    [InlineData(new[] { "plugin", "list", "--help" }, "https://go.devproxy.net?go=cli-plugin-list")]
    public async Task RunStandaloneAsync_Help_PrintsDocsFooterAfterExamples(string[] args, string expectedLink)
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        int exitCode;
        try
        {
            exitCode = await PluginCommand.RunStandaloneAsync([.. args, "--log-level", "None"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Docs: {expectedLink}", lines[^1]);
        if (args.Contains("list"))
        {
            Assert.True(text.IndexOf("Examples:", StringComparison.Ordinal) < text.IndexOf("Docs:", StringComparison.Ordinal));
        }
    }
}
