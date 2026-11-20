// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Starts Dev Proxy detached (never as system proxy, never installing the certificate),
/// proxies a request through a mock plugin, and manages the instance with status/stop.
/// </summary>
public sealed class DetachedLifecycleScenarios
{
    private static readonly string[] s_startArgs =
    [
        "--detach", "--as-system-proxy", "false", "--port", "0", "--api-port", "0",
        "--install-cert", "false", "--no-first-run",
    ];

    [Fact]
    public async Task TextOutput_StartProxyStatusStop()
    {
        await using var sandbox = new TestSandbox();

        var start = await DevProxyCli.RunAsync(sandbox, s_startArgs);
        TrackStartedInstance(sandbox, start);
        Assert.True(start.ExitCode == 0, start.ToString());
        var pid = int.Parse(Field(start, "PID"), CultureInfo.InvariantCulture);
        var proxyUrl = AssertRealPort(Field(start, "Proxy URL"), start);
        var apiUrl = AssertRealPort(Field(start, "API URL"), start);

        await AssertMockedThroughProxyAsync(proxyUrl);
        await AssertApiListeningAsync(apiUrl);

        var status = await DevProxyCli.RunAsync(sandbox, "status", "--pid", TestSandbox.Pid(pid));
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal(proxyUrl, AssertRealPort(Field(status, "Proxy URL"), status));
        Assert.Equal(apiUrl, AssertRealPort(Field(status, "API URL"), status));
        Assert.Equal("No", Field(status, "System proxy"));
        Assert.Equal("No", Field(status, "Recording"));

        await StopAndAssertStoppedAsync(sandbox, pid);
    }

    [Fact]
    public async Task JsonOutput_StartProxyStatusStop()
    {
        await using var sandbox = new TestSandbox();

        var start = await DevProxyCli.RunAsync(sandbox, [.. s_startArgs, "--output", "json"]);
        TrackStartedInstance(sandbox, start);
        Assert.True(start.ExitCode == 0, start.ToString());
        Assert.Empty(start.StdErrLines);
        var started = Assert.Single(DevProxyCli.ParseJsonLines(start.StdOutLines, start));
        Assert.Equal("result", started.GetProperty("type").GetString());
        var data = started.GetProperty("data");
        var pid = data.GetProperty("pid").GetInt32();
        var proxyUrl = AssertRealPort(data.GetProperty("proxyUrl").GetString(), start);
        var apiUrl = AssertRealPort(data.GetProperty("apiUrl").GetString(), start);

        await AssertMockedThroughProxyAsync(proxyUrl);
        await AssertApiListeningAsync(apiUrl);

        var status = await DevProxyCli.RunAsync(sandbox, "status", "--pid", TestSandbox.Pid(pid), "--output", "json");
        Assert.True(status.ExitCode == 0, status.ToString());
        var statusData = Assert.Single(DevProxyCli.ParseJsonLines(status.StdOutLines, status)).GetProperty("data");
        Assert.Equal(pid, statusData.GetProperty("pid").GetInt32());
        Assert.Equal(proxyUrl, AssertRealPort(statusData.GetProperty("proxyUrl").GetString(), status));
        Assert.Equal(apiUrl, AssertRealPort(statusData.GetProperty("apiUrl").GetString(), status));
        Assert.Equal("available", statusData.GetProperty("apiStatus").GetString());
        Assert.False(statusData.GetProperty("asSystemProxy").GetBoolean());
        Assert.False(statusData.GetProperty("recording").GetBoolean());
        // Compare the unique tail: temp folders may be reported through a resolved symlink (macOS).
        Assert.EndsWith(sandbox.RelativeConfigPath, statusData.GetProperty("configFile").GetString(),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        await StopAndAssertStoppedAsync(sandbox, pid);

        var stopped = await DevProxyCli.RunAsync(sandbox, "status", "--pid", TestSandbox.Pid(pid), "--output", "json");
        var stoppedData = Assert.Single(DevProxyCli.ParseJsonLines(stopped.StdOutLines, stopped)).GetProperty("data");
        Assert.False(stoppedData.GetProperty("running").GetBoolean());
    }

    // Registers the daemon for teardown before any assertion on the start output can fail.
    // Matches both the text ("PID: 123", "Log file: ...") and JSON ("pid":123, "logFile":"...") forms.
    private static void TrackStartedInstance(TestSandbox sandbox, CliResult start)
    {
        var pid = Regex.Match(start.StdOut, @"(?:PID:\s*|""pid"":)(\d+)");
        if (!pid.Success)
        {
            return;
        }
        var log = Regex.Match(start.StdOut, @"(?:Log file:\s*(.+?)\s*$|""logFile"":""((?:[^""\\]|\\.)*)"")", RegexOptions.Multiline);
        var logFile = !log.Success ? null
            : log.Groups[1].Success ? log.Groups[1].Value
            : System.Text.Json.JsonSerializer.Deserialize<string>($"\"{log.Groups[2].Value}\"");
        sandbox.TrackInstance(int.Parse(pid.Groups[1].Value, CultureInfo.InvariantCulture), logFile);
    }

    private static async Task StopAndAssertStoppedAsync(TestSandbox sandbox, int pid)
    {
        var stop = await DevProxyCli.RunAsync(sandbox, "stop", "--pid", TestSandbox.Pid(pid));
        Assert.True(stop.ExitCode == 0, stop.ToString());

        var status = await DevProxyCli.RunAsync(sandbox, "status", "--pid", TestSandbox.Pid(pid));
        Assert.True(status.ExitCode == 1, status.ToString());
    }

    private static async Task AssertMockedThroughProxyAsync(Uri proxyUrl)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxyUrl), UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using var response = await client.GetAsync(new Uri(TestSandbox.MockedUrl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestSandbox.MockedBody, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertApiListeningAsync(Uri apiUrl)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // Unauthenticated requests are rejected, which proves the API is bound to this port.
        using var response = await client.GetAsync(new Uri(apiUrl, "/proxy"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Uri AssertRealPort(string? url, CliResult result)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), $"'{url}' isn't a URL.\n{result}");
        Assert.True(uri.Port > 0, $"'{url}' doesn't have the port the OS assigned for port 0.\n{result}");
        return uri;
    }

    private static string Field(CliResult result, string label)
    {
        var match = Regex.Match(result.StdOut, $@"^\s*{Regex.Escape(label)}:\s+(.+?)\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"Output has no '{label}:' line.\n{result}");
        return match.Groups[1].Value;
    }
}
