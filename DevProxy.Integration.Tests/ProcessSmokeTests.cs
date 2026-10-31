// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// End-to-end process-level smoke test. Unlike the in-process plugin suites (which boot the
/// engine/plugins directly), this spawns the REAL <c>DevProxy</c> host executable with a
/// config file and asserts that:
/// <list type="bullet">
///   <item>config-driven plugin loading works (the host reads <c>devproxyrc.json</c>, loads
///   <c>MockResponsePlugin</c> from disk via <c>pluginPath</c>, and binds its config section);</item>
///   <item>the Kestrel engine starts, registers as an explicit HTTP proxy, and a real request
///   routed through it is short-circuited by the mock — proving the whole wire is intact after
///   the Titanium → Kestrel cut-over.</item>
/// </list>
/// Uses a plain-HTTP watched URL so no TLS interception / CA trust is needed; the mock
/// short-circuits before any forward, so the (non-existent) origin is never contacted.
/// </summary>
[Collection("process-smoke")]
public sealed class ProcessSmokeTests
{
    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromSeconds(90);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_EnvironmentOverrides_PreserveTildeConfigPathAndApplyBeforeAnnouncement(bool useCommandLine)
    {
        var host = Path.Combine(Path.GetDirectoryName(LocateHostDll())!,
            OperatingSystem.IsWindows() ? "devproxy.exe" : "devproxy");
        Assert.True(File.Exists(host), "Build DevProxy before running process smoke tests.");

        var workDir = Directory.CreateTempSubdirectory("devproxy-startup-env-");
        var managementDir = workDir.CreateSubdirectory("management");
        var overrideHome = workDir.CreateSubdirectory("override-home");
        using var inheritedProxy = new TcpListener(IPAddress.Loopback, 0);
        using var effectiveProxy = new TcpListener(IPAddress.Loopback, 0);
        inheritedProxy.Start();
        effectiveProxy.Start();
        var inheritedProxyUrl = $"http://127.0.0.1:{((IPEndPoint)inheritedProxy.LocalEndpoint).Port}";
        var effectiveProxyUrl = $"http://127.0.0.1:{((IPEndPoint)effectiveProxy.LocalEndpoint).Port}";
        var inheritedEnvironment = new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = inheritedProxyUrl,
            ["HTTPS_PROXY"] = inheritedProxyUrl,
            ["ALL_PROXY"] = inheritedProxyUrl,
            ["NO_PROXY"] = "",
            ["http_proxy"] = inheritedProxyUrl,
            ["https_proxy"] = inheritedProxyUrl,
            ["all_proxy"] = inheritedProxyUrl,
            ["no_proxy"] = "",
        };
        var overrides = new Dictionary<string, string>
        {
            ["HOME"] = overrideHome.FullName,
            ["http_proxy"] = effectiveProxyUrl,
            ["https_proxy"] = effectiveProxyUrl,
            ["all_proxy"] = effectiveProxyUrl,
            ["no_proxy"] = "",
        };
        int? pid = null;
        string? logFile = null;
        using var proxyTimeout = new CancellationTokenSource(s_startupTimeout);
        var requests = new ConcurrentQueue<string>();
        var inheritedRequests = new ConcurrentQueue<string>();
        var proxyRequests = RejectProxyRequestsAsync(effectiveProxy, requests, proxyTimeout.Token);
        var inheritedProxyRequests = RejectProxyRequestsAsync(inheritedProxy, inheritedRequests, proxyTimeout.Token);
        try
        {
            var config = JsonNode.Parse(ConfigFile)!.AsObject();
            config.Remove("$schema");
            config["installCert"] = false;
            config["noFirstRun"] = true;
            config["validateSchemas"] = false;
            config["mocksPlugin"]!["mocksFile"] = Path.Combine(workDir.FullName, "mocks.json");
            var mocks = JsonNode.Parse(MocksFile)!.AsObject();
            mocks.Remove("$schema");
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "mocks.json"), mocks.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(managementDir.FullName, "devproxyrc.json"), config.ToJsonString());
            if (!useCommandLine)
            {
                config["env"] = JsonSerializer.SerializeToNode(overrides);
            }
            var configPath = Path.Combine(workDir.FullName, "devproxyrc.json");
            await File.WriteAllTextAsync(configPath, config.ToJsonString());
            var tildePath = "~/" + Path.GetRelativePath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), configPath);
            var args = new List<string>
            {
                "--detach", "--as-system-proxy", "false", "--port", "0", "--api-port", "0",
                "--install-cert", "false", "--no-first-run", "--output", "json", "-c", tildePath,
            };
            if (useCommandLine)
            {
                args.Add("--env");
                args.AddRange(overrides.Select(variable => $"{variable.Key}={variable.Value}"));
            }

            var startup = await RunCommandAsync(host, workDir.FullName, args, inheritedEnvironment);
            Assert.Equal(0, startup.ExitCode);
            var started = GetResultData(startup.Output);
            pid = started.GetProperty("pid").GetInt32();
            logFile = started.GetProperty("logFile").GetString();

            Assert.Contains("CONNECT aka.ms:443 HTTP/1.1", requests);
            Assert.Empty(inheritedRequests);

            var status = await RunCommandAsync(host, managementDir.FullName,
                ["status", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", "json"]);
            Assert.Equal(0, status.ExitCode);
            var statusData = GetResultData(status.Output);
            Assert.Equal(configPath, statusData.GetProperty("configFile").GetString());
            Assert.Equal("available", statusData.GetProperty("apiStatus").GetString());

            if (!useCommandLine)
            {
                Assert.NotNull(logFile);
                Assert.DoesNotContain("HTTP proxy settings are cached", await File.ReadAllTextAsync(logFile), StringComparison.Ordinal);
                config["env"]!.AsObject().Remove("https_proxy");
                config["env"]!["no_proxy"] = "api.contoso.local";
                await Task.Delay(600);
                await File.WriteAllTextAsync(configPath, config.ToJsonString());

                using var reloadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string logs;
                do
                {
                    await Task.Delay(100, reloadTimeout.Token);
                    logs = await File.ReadAllTextAsync(logFile, reloadTimeout.Token);
                } while (!logs.Contains("HTTP proxy settings are cached", StringComparison.Ordinal));

                var warning = logs.Split('\n').Single(line => line.Contains("HTTP proxy settings are cached", StringComparison.Ordinal));
                Assert.Contains(OperatingSystem.IsWindows() ? "HTTPS_PROXY" : "https_proxy", warning, StringComparison.Ordinal);
                Assert.Contains(OperatingSystem.IsWindows() ? "NO_PROXY" : "no_proxy", warning, StringComparison.Ordinal);
                Assert.Contains("Stop Dev Proxy and start it again", warning, StringComparison.Ordinal);
                Assert.DoesNotContain(inheritedProxyUrl, warning, StringComparison.Ordinal);
                Assert.DoesNotContain(effectiveProxyUrl, warning, StringComparison.Ordinal);

                while (true)
                {
                    await Task.Delay(100, reloadTimeout.Token);
                    var reloadedStatus = await RunCommandAsync(host, managementDir.FullName,
                        ["status", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", "json"]);
                    if (reloadedStatus.ExitCode == 0)
                    {
                        var data = GetResultData(reloadedStatus.Output);
                        if (data.GetProperty("apiStatus").GetString() == "available" &&
                            data.GetProperty("startedAt").GetString() != statusData.GetProperty("startedAt").GetString())
                        {
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (pid.HasValue)
                {
                    var stopped = await RunCommandAsync(host, managementDir.FullName,
                        ["stop", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                    Assert.Equal(0, stopped.ExitCode);
                }
            }
            finally
            {
                await proxyTimeout.CancelAsync();
                try
                {
                    await Task.WhenAll(proxyRequests, inheritedProxyRequests);
                }
                catch (OperationCanceledException) when (proxyTimeout.IsCancellationRequested)
                {
                }
                if (logFile is not null)
                {
                    File.Delete(logFile);
                }
                workDir.Delete(recursive: true);
            }
        }
    }

    private static async Task RejectProxyRequestsAsync(
        TcpListener listener, ConcurrentQueue<string> requests, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            using var reader = new StreamReader(connection.GetStream(), leaveOpen: true);
            var request = await reader.ReadLineAsync(cancellationToken);
            if (request is not null)
            {
                requests.Enqueue(request);
            }
            await connection.GetStream().WriteAsync(
                "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                cancellationToken);
        }
    }

    [Theory]
    [InlineData("text", "cli")]
    [InlineData("json", "cli")]
    [InlineData("text", "config")]
    [InlineData("json", "config")]
    [InlineData("text", null)]
    [InlineData("json", null)]
    public async Task Status_HonorsInstanceTokenSuppression_ExplicitRetrievalRemainsAvailable(string output, string? ciSource)
    {
        var host = Path.Combine(Path.GetDirectoryName(LocateHostDll())!,
            OperatingSystem.IsWindows() ? "devproxy.exe" : "devproxy");
        Assert.True(File.Exists(host), "Build DevProxy before running process smoke tests.");

        var workDir = Directory.CreateTempSubdirectory("devproxy-status-");
        var managementDir = workDir.CreateSubdirectory("management");
        int? pid = null;
        string? logFile = null;
        try
        {
            var config = JsonNode.Parse(ConfigFile)!.AsObject();
            config["installCert"] = false;
            config["noFirstRun"] = true;
            config["validateSchemas"] = false;
            config["mocksPlugin"]!["mocksFile"] = Path.Combine(workDir.FullName, "mocks.json");
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "mocks.json"), MocksFile);
            await File.WriteAllTextAsync(Path.Combine(managementDir.FullName, "devproxyrc.json"), config.ToJsonString());
            if (ciSource == "config")
            {
                config["env"] = new JsonObject { ["CI"] = "1" };
            }
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "devproxyrc.json"), config.ToJsonString());

            var args = new List<string>
            {
                "--detach", "--as-system-proxy", "false", "--port", "0", "--api-port", "0",
                "--install-cert", "false", "--no-first-run", "--output", "json",
            };
            if (ciSource == "cli")
            {
                args.AddRange(["--env", "CI=1"]);
            }
            var startup = await RunCommandAsync(host, workDir.FullName, args);
            Assert.Equal(0, startup.ExitCode);
            var started = GetResultData(startup.Output);
            pid = started.GetProperty("pid").GetInt32();
            logFile = started.GetProperty("logFile").GetString();

            var tokenResult = await RunCommandAsync(host, managementDir.FullName,
                ["api", "token", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            Assert.Equal(0, tokenResult.ExitCode);
            var token = tokenResult.Output.Split('\n')
                .Select(line => line.Trim())
                .Single(line => line.Length == 64 && line.All(Uri.IsHexDigit));

            var status = await RunCommandAsync(host, managementDir.FullName,
                ["status", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", output]);
            Assert.Equal(0, status.ExitCode);
            var shouldDisplayToken = ciSource is null;
            Assert.Equal(shouldDisplayToken, status.Output.Contains(token, StringComparison.Ordinal));
            if (output == "json")
            {
                var data = GetResultData(status.Output);
                Assert.Equal("available", data.GetProperty("apiStatus").GetString());
                Assert.Equal(shouldDisplayToken, data.TryGetProperty("token", out _));
            }
            else
            {
                Assert.Contains("Dev Proxy is running.", status.Output, StringComparison.Ordinal);
                Assert.Equal(shouldDisplayToken, status.Output.Contains("API token:", StringComparison.Ordinal));
            }
        }
        finally
        {
            try
            {
                if (pid.HasValue)
                {
                    var stopped = await RunCommandAsync(host, managementDir.FullName,
                        ["stop", "--pid", pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                    Assert.Equal(0, stopped.ExitCode);
                }
            }
            finally
            {
                if (logFile is not null)
                {
                    File.Delete(logFile);
                }
                workDir.Delete(recursive: true);
            }
        }
    }

    private static JsonElement GetResultData(string output)
    {
        foreach (var line in output.Split('\n').Where(line => line.StartsWith('{')))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("type").GetString() == "result")
            {
                return json.RootElement.GetProperty("data").Clone();
            }
        }
        throw new InvalidOperationException("Command did not emit a JSON result.");
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string host, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(host)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Remove("CI");
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(s_startupTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Host_LoadsMockPluginFromConfig_AndServesMock()
    {
        var hostDll = LocateHostDll();
        Assert.True(File.Exists(hostDll), $"DevProxy host not built at {hostDll}. Run `dotnet build DevProxy` first.");

        var workDir = Directory.CreateTempSubdirectory("devproxy-smoke-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "mocks.json"), MocksFile);
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "devproxyrc.json"), ConfigFile);

            using var process = StartHost(hostDll, workDir.FullName);
            try
            {
                var port = await WaitForListeningPortAsync(process);

                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                    UseProxy = true,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

                using var response = await client.GetAsync(new Uri("http://api.contoso.local/hello"));
                var body = await response.Content.ReadAsStringAsync();

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("hello from the mock", body, StringComparison.Ordinal);
            }
            finally
            {
                StopHost(process);
            }
        }
        finally
        {
            workDir.Delete(recursive: true);
            // The host writes a timestamped log to the working dir's parent in some modes;
            // the temp dir delete above covers logs written under workDir. Best-effort.
        }
    }

    [Fact]
    public async Task Stdio_BackToBackJsonRpcMessages_AreMockedIndividually()
    {
        var hostDll = LocateHostDll();
        Assert.True(File.Exists(hostDll), $"DevProxy host not built at {hostDll}. Run `dotnet build DevProxy` first.");

        var workDir = Directory.CreateTempSubdirectory("devproxy-stdio-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "stdio-mocks.json"), StdioMocksFile);
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "devproxyrc.json"), StdioConfigFile);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workDir.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(hostDll);
            psi.ArgumentList.Add("stdio");
            psi.ArgumentList.Add("--config-file");
            psi.ArgumentList.Add(Path.Combine(workDir.FullName, "devproxyrc.json"));
            psi.ArgumentList.Add("--");
            // Any child that stays alive while reading stdin; unmocked messages are blocked
            // so it never receives input.
            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("cmd.exe");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("more");
            }
            else
            {
                psi.ArgumentList.Add("cat");
            }

            using var process = new Process { StartInfo = psi };
            Assert.True(process.Start(), "Failed to start DevProxy stdio process.");
            try
            {
                // Write an id-less notification and an id-bearing request in a single write so
                // that they arrive in the same stdin read (#1757).
                await process.StandardInput.WriteAsync(
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"accept_eula\"}}\n");
                await process.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(s_startupTimeout);
                var line = await ReadLineAsync(process.StandardOutput, cts.Token);
                Assert.NotNull(line);

                using var response = JsonDocument.Parse(line);
                Assert.Equal(2, response.RootElement.GetProperty("id").GetInt32());
            }
            finally
            {
                StopHost(process);
            }
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stdio_LargeMessageThroughStreamingChild_DoesNotDeadlock()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var hostDll = LocateHostDll();
        Assert.True(File.Exists(hostDll), $"DevProxy host not built at {hostDll}. Run `dotnet build DevProxy` first.");

        var workDir = Directory.CreateTempSubdirectory("devproxy-stdio-large-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "stdio-mocks.json"), StdioMocksFile);
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "devproxyrc.json"),
                StdioConfigFile.Replace("\"blockUnmockedRequests\": true", "\"blockUnmockedRequests\": false", StringComparison.Ordinal));

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workDir.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(hostDll);
            psi.ArgumentList.Add("stdio");
            psi.ArgumentList.Add("--config-file");
            psi.ArgumentList.Add(Path.Combine(workDir.FullName, "devproxyrc.json"));
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("cat");

            using var process = new Process { StartInfo = psi };
            Assert.True(process.Start(), "Failed to start DevProxy stdio process.");
            try
            {
                // Larger than typical pipe buffers so the child blocks writing stdout
                // while the proxy is still writing the message to its stdin.
                var line = new string('x', 1024 * 1024);
                using var cts = new CancellationTokenSource(s_startupTimeout);
                var readTask = ReadLineAsync(process.StandardOutput, cts.Token);
                await process.StandardInput.WriteAsync(line + "\n");
                await process.StandardInput.FlushAsync();

                Assert.Equal(line, await readTask);
            }
            finally
            {
                StopHost(process);
            }
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static Process StartHost(string hostDll, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hostDll);
        psi.ArgumentList.Add("--config-file");
        psi.ArgumentList.Add(Path.Combine(workDir, "devproxyrc.json"));
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--api-port");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--as-system-proxy");
        psi.ArgumentList.Add("false");
        psi.ArgumentList.Add("--no-first-run");

        var process = new Process { StartInfo = psi };
        Assert.True(process.Start(), "Failed to start DevProxy host process.");
        return process;
    }

    /// <summary>
    /// Reads the host's stdout until it logs the bound proxy port
    /// (<c>listening on 127.0.0.1:&lt;port&gt;</c>) or the startup timeout elapses.
    /// </summary>
    private static async Task<int> WaitForListeningPortAsync(Process process)
    {
        using var cts = new CancellationTokenSource(s_startupTimeout);
        var captured = new System.Text.StringBuilder();

        while (!cts.IsCancellationRequested)
        {
            var line = await ReadLineAsync(process.StandardOutput, cts.Token);
            if (line is null)
            {
                // stdout closed — the process exited before listening.
                var err = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                throw new InvalidOperationException(
                    $"DevProxy host exited before listening.\nSTDOUT:\n{captured}\nSTDERR:\n{err}");
            }

            _ = captured.AppendLine(line);
            var marker = "listening on 127.0.0.1:";
            var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var portText = new string(line[(idx + marker.Length)..]
                    .TakeWhile(char.IsDigit)
                    .ToArray());
                if (int.TryParse(portText, out var port) && port > 0)
                {
                    return port;
                }
            }
        }

        throw new TimeoutException(
            $"DevProxy host did not report a listening port within {s_startupTimeout.TotalSeconds}s.\nSTDOUT:\n{captured}");
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static void StopHost(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already gone.
        }
    }

    /// <summary>
    /// Walks up from the test assembly output to the repo root, then resolves the host's
    /// build output for the same configuration (Debug/Release).
    /// </summary>
    private static string LocateHostDll()
    {
        var config =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DevProxy", "bin", config, "net10.0", "devproxy.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // Fall back to a path relative to the repo root guess so the assert message is useful.
        return Path.Combine(AppContext.BaseDirectory, "devproxy.dll");
    }

    private const string StdioConfigFile = """
        {
          "plugins": [
            {
              "name": "MockStdioResponsePlugin",
              "enabled": true,
              "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
              "configSection": "mockStdioResponsePlugin"
            }
          ],
          "mockStdioResponsePlugin": {
            "mocksFile": "stdio-mocks.json",
            "blockUnmockedRequests": true
          },
          "validateSchemas": false
        }
        """;

    private const string StdioMocksFile = """
        {
          "mocks": [
            {
              "request": { "bodyFragment": "accept_eula" },
              "response": { "stdout": "{\"jsonrpc\":\"2.0\",\"id\":@stdin.body.id,\"result\":{}}\n" }
            }
          ]
        }
        """;

    private const string ConfigFile = """
        {
          "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v3.1.0/rc.schema.json",
          "plugins": [
            {
              "name": "MockResponsePlugin",
              "enabled": true,
              "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
              "configSection": "mocksPlugin"
            }
          ],
          "urlsToWatch": [
            "http://api.contoso.local/*"
          ],
          "mocksPlugin": {
            "mocksFile": "mocks.json"
          },
          "logLevel": "information"
        }
        """;

    private const string MocksFile = """
        {
          "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v3.1.0/mockresponseplugin.mocksfile.schema.json",
          "mocks": [
            {
              "request": {
                "url": "http://api.contoso.local/hello"
              },
              "response": {
                "statusCode": 200,
                "body": "hello from the mock"
              }
            }
          ]
        }
        """;
}
