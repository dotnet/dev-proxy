// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Spawns the real DevProxy host to verify that CLI commands return a non-zero
/// exit code when they fail, so scripts and agents can rely on it.
/// </summary>
[Collection("process-smoke")]
public sealed class CommandExitCodeTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task ConfigNew_Succeeds_ReturnsZero()
    {
        using var workDir = new TempDir();

        var (exitCode, output) = await RunHostAsync(workDir.Path, "config", "new", "myconfig.json");

        Assert.True(exitCode == 0, output);
        Assert.True(File.Exists(Path.Combine(workDir.Path, "myconfig.json")), output);
    }

    [Fact]
    public async Task ConfigNew_Fails_ReturnsNonZero()
    {
        using var workDir = new TempDir();

        var (exitCode, output) = await RunHostAsync(workDir.Path, "config", "new", Path.Combine("missing-folder", "myconfig.json"));

        Assert.True(exitCode != 0, output);
    }

    [Fact]
    public async Task ConfigGet_DownloadFails_ReturnsNonZero()
    {
        using var workDir = new TempDir();
        var configId = $"devproxy-exitcode-test-{Guid.NewGuid():N}";
        // Route the GitHub request through an unreachable proxy so the download fails without network access.
        var env = new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = "http://127.0.0.1:1",
            ["HTTP_PROXY"] = "http://127.0.0.1:1",
            ["NO_PROXY"] = "",
        };

        try
        {
            var (exitCode, output) = await RunHostAsync(workDir.Path, env, "config", "get", configId);

            Assert.True(exitCode != 0, output);
        }
        finally
        {
            var configsFolder = Path.Combine(DevProxy.Abstractions.Utils.ProxyUtils.DataFolder, "configs");
            if (Directory.Exists(configsFolder))
            {
                foreach (var dir in Directory.GetDirectories(configsFolder, $"{configId}*"))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task MocksFromHttpResponses_Succeeds_ReturnsZero()
    {
        using var workDir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(workDir.Path, "devproxyrc.json"), ConfigFile);
        await File.WriteAllTextAsync(Path.Combine(workDir.Path, "response.http"), "HTTP/1.1 200 OK\nContent-Type: text/plain\n\nhello");

        var (exitCode, output) = await RunHostAsync(workDir.Path, "mocks", "from-http-responses", "*.http", "--mocks-file", "mocks.json");

        Assert.True(exitCode == 0, output);
        Assert.True(File.Exists(Path.Combine(workDir.Path, "mocks.json")), output);
    }

    [Fact]
    public async Task MocksFromHttpResponses_NoMatchingFiles_ReturnsNonZero()
    {
        using var workDir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(workDir.Path, "devproxyrc.json"), ConfigFile);

        var (exitCode, output) = await RunHostAsync(workDir.Path, "mocks", "from-http-responses", "*.http", "--mocks-file", "mocks.json");

        Assert.True(exitCode != 0, output);
    }

    [Fact]
    public async Task MocksFromHttpResponses_InvalidFile_ReturnsNonZero()
    {
        using var workDir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(workDir.Path, "devproxyrc.json"), ConfigFile);
        await File.WriteAllTextAsync(Path.Combine(workDir.Path, "response.http"), "not an http response");

        var (exitCode, output) = await RunHostAsync(workDir.Path, "mocks", "from-http-responses", "*.http", "--mocks-file", "mocks.json");

        Assert.True(exitCode != 0, output);
    }

    private static Task<(int ExitCode, string Output)> RunHostAsync(string workDir, params string[] args) =>
        RunHostAsync(workDir, new Dictionary<string, string>(), args);

    private static async Task<(int ExitCode, string Output)> RunHostAsync(string workDir, Dictionary<string, string> env, params string[] args)
    {
        var hostDll = LocateHostDll();
        Assert.True(File.Exists(hostDll), $"DevProxy host not built at {hostDll}. Run `dotnet build DevProxy` first.");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hostDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start(), "Failed to start DevProxy host process.");
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(s_timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"DevProxy host did not exit within {s_timeout.TotalSeconds}s.");
        }

        var output = $"STDOUT:\n{await stdout}\nSTDERR:\n{await stderr}";
        return (process.ExitCode, output);
    }

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

        return Path.Combine(AppContext.BaseDirectory, "DevProxy.dll");
    }

    private sealed class TempDir : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("devproxy-exitcode-");

        public string Path => _dir.FullName;

        public void Dispose()
        {
            try
            {
                _dir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private const string ConfigFile = """
        {
          "plugins": [
            {
              "name": "MockResponsePlugin",
              "enabled": true,
              "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
            }
          ],
          "urlsToWatch": [
            "https://api.contoso.local/*"
          ]
        }
        """;
}
