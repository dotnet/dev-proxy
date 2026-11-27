// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Tests;

// Captures the process-wide console, so it mustn't run in parallel with other management tests.
[Collection("management-environment")]
public class SystemProxyCommandTests
{
    [Fact]
    public void ConsoleLoggerFactory_JsonOutputAboveInformation_KeepsResultAndDropsDiagnostics()
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            using (var loggerFactory = SystemProxyCommand.CreateConsoleLoggerFactory(OutputFormat.Json, LogLevel.Warning))
            {
                var logger = loggerFactory.CreateLogger<SystemProxyCommand>();
                logger.LogInformation("diagnostic-below-level");
                logger.LogStructuredOutput("""{"message":"reset-result"}""");
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = stdout.ToString();
        Assert.Contains("reset-result", output, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-below-level", output, StringComparison.Ordinal);
    }
}
