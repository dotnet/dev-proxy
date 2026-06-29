// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net;
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
            var candidate = Path.Combine(dir.FullName, "DevProxy", "bin", config, "net10.0", "DevProxy.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // Fall back to a path relative to the repo root guess so the assert message is useful.
        return Path.Combine(AppContext.BaseDirectory, "DevProxy.dll");
    }

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
