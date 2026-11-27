// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Xunit;

namespace DevProxy.Tests;

public sealed class RootCertificateTrustTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PromptForTrust_JsonOutput_WritesNothingToStdout(bool isNonInteractive)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader("y\n");

        _ = RootCertificateTrust.PromptForTrust(OutputFormat.Json, stdout, stderr, input, isNonInteractive);

        Assert.Empty(stdout.ToString());
        Assert.Contains("self-signed certificate", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptForTrust_JsonOutputInteractive_WritesQuestionToStderrAndReadsAnswer()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader(" n \n");

        var answer = RootCertificateTrust.PromptForTrust(OutputFormat.Json, stdout, stderr, input, isNonInteractive: false);

        Assert.Equal("n", answer);
        Assert.Empty(stdout.ToString());
        Assert.Contains("(Y/n)", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptForTrust_TextOutput_WritesPromptToStdout()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader("y\n");

        var answer = RootCertificateTrust.PromptForTrust(OutputFormat.Text, stdout, stderr, input, isNonInteractive: false);

        Assert.Equal("y", answer);
        Assert.Empty(stderr.ToString());
        Assert.Contains("self-signed certificate", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("(Y/n)", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptForTrust_NonInteractive_DefaultsToTrustWithoutReadingInput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader("n\n");

        var answer = RootCertificateTrust.PromptForTrust(OutputFormat.Text, stdout, stderr, input, isNonInteractive: true);

        Assert.Equal("y", answer);
        Assert.DoesNotContain("(Y/n)", stdout.ToString(), StringComparison.Ordinal);
    }
}
