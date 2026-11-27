// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Logging;
using Xunit;

namespace DevProxy.Tests;

public class SystemProxyRecoveryEchoTests
{
    private const string Restoring = "Restoring the system proxy left by a crashed Dev Proxy instance...";
    private const string AuthWarning = "Restored the HTTP proxy corp.example:8080 for Wi-Fi, but its password can't be restored. Turn on 'Proxy server requires password' for Wi-Fi in System Settings > Network > Details > Proxies and enter your credentials.";
    private const string Recovered = "Recovered system proxy left by a crashed Dev Proxy instance (PID: 4242).";

    [Fact]
    public async Task TextOutput_EchoesRecoveryRecordsIncludingAuthWarning()
    {
        var log = string.Join("\r\n",
            " info    Dev Proxy is starting",
            " info    " + Restoring,
            " warn    " + AuthWarning,
            " info    " + Recovered,
            " info    Listening on 127.0.0.1:8000",
            "");

        var (stdout, stderr) = await EchoAsync(log, isJsonOutput: false);

        Assert.Equal([" info    " + Restoring, " info    " + Recovered], stdout);
        Assert.Equal([" warn    " + AuthWarning], stderr);
    }

    [Fact]
    public async Task TextOutput_SendsExceptionDetailsWithTheirRecord()
    {
        var log = string.Join("\n",
            " info    " + Restoring,
            " fail    Failed to restore the system proxy via networksetup -setwebproxy Wi-Fi.",
            "System.InvalidOperationException: networksetup failed",
            " fail    Couldn't restore the system proxy left by a crashed Dev Proxy instance (PID: 4242).");

        var (stdout, stderr) = await EchoAsync(log, isJsonOutput: false);

        Assert.Equal([" info    " + Restoring], stdout);
        Assert.Equal(3, stderr.Count);
        Assert.Equal("System.InvalidOperationException: networksetup failed", stderr[1]);
    }

    [Fact]
    public async Task JsonOutput_EchoesRecoveryRecordsIncludingAuthWarningToStdout()
    {
        string Record(string level, string message) =>
            $$"""{"type":"log","level":"{{level}}","message":"{{message}}"}""";
        var log = string.Join("\n",
            Record("info", "Dev Proxy is starting"),
            Record("info", Restoring),
            Record("warning", AuthWarning),
            Record("info", Recovered),
            Record("info", "Listening on 127.0.0.1:8000"));

        var (stdout, stderr) = await EchoAsync(log, isJsonOutput: true);

        Assert.Equal([Record("info", Restoring), Record("warning", AuthWarning), Record("info", Recovered)], stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task NoRecovery_WritesNothing()
    {
        var (stdout, stderr) = await EchoAsync(" warn    Some plugin warning\n info    Listening on 127.0.0.1:8000", isJsonOutput: false);

        Assert.Empty(stdout);
        Assert.Empty(stderr);
    }

    private static async Task<(List<string> Stdout, List<string> Stderr)> EchoAsync(string log, bool isJsonOutput)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await SystemProxyRecoveryEcho.WriteAsync(log, isJsonOutput, stdout, stderr);

        return (Lines(stdout), Lines(stderr));

        static List<string> Lines(StringWriter writer) =>
            [.. writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)];
    }
}
