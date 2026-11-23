// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DevProxy.Cli.Tests;

sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public IEnumerable<string> StdOutLines => SplitLines(StdOut);
    public IEnumerable<string> StdErrLines => SplitLines(StdErr);

    public override string ToString() =>
        $"Exit code: {ExitCode}\nSTDOUT:\n{StdOut}\nSTDERR:\n{StdErr}";

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Runs the Dev Proxy executable as a black-box subprocess.
/// <para>
/// Set <c>DEVPROXY_EXE</c> to test a specific binary (for example the published PR build in
/// CI). Otherwise the tests use the host from this repo's build output.
/// </para>
/// </summary>
static class DevProxyCli
{
    public const string ExecutableEnvironmentVariable = "DEVPROXY_EXE";

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(5);
    private static readonly Lazy<string> s_executable = new(LocateExecutable);

    public static string Executable => s_executable.Value;

    public static async Task<CliResult> RunAsync(
        TestSandbox sandbox,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = sandbox.WorkDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Settings like DEV_PROXY_CERT_PATH from the developer's shell would point the
        // run back at their real files; only the sandbox decides them.
        foreach (var name in startInfo.Environment.Keys
            .Where(n => n.StartsWith("DEV_PROXY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _ = startInfo.Environment.Remove(name);
        }
        foreach (var (name, value) in sandbox.Environment)
        {
            startInfo.Environment[name] = value;
        }
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
        try
        {
            _ = process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new CliLaunchException($"Couldn't start '{startInfo.FileName}': {ex.Message}", ex);
        }
        // Closed stdin: no interactive prompts or hotkeys, like in CI or an agent shell.
        process.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var drain = new CancellationTokenSource();
        var stdoutReader = CopyLinesAsync(process.StandardOutput, stdout, drain.Token);
        var stderrReader = CopyLinesAsync(process.StandardError, stderr, drain.Token);

        using var timeoutCts = new CancellationTokenSource(timeout ?? s_defaultTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"devproxy {string.Join(' ', startInfo.ArgumentList)} didn't exit within {(timeout ?? s_defaultTimeout).TotalSeconds}s.\n" +
                $"STDOUT:\n{Snapshot(stdout)}\nSTDERR:\n{Snapshot(stderr)}");
        }

        // A detached daemon may inherit the output handles (Windows), so don't wait
        // for EOF forever once the process we started has exited.
        drain.CancelAfter(s_drainTimeout);
        await Task.WhenAll(stdoutReader, stderrReader);

        return new CliResult(process.ExitCode, Snapshot(stdout), Snapshot(stderr));
    }

    public static Task<CliResult> RunAsync(TestSandbox sandbox, params string[] args) =>
        RunAsync(sandbox, (IEnumerable<string>)args);

    /// <summary>
    /// Asserts that every non-empty line is a standalone JSON object and returns them.
    /// </summary>
    public static IReadOnlyList<JsonElement> ParseJsonLines(IEnumerable<string> lines, CliResult result)
    {
        var parsed = new List<JsonElement>();
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                Xunit.Assert.True(document.RootElement.ValueKind == JsonValueKind.Object,
                    $"Expected a JSON object per line but got: {line}\n{result}");
                parsed.Add(document.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                Xunit.Assert.Fail($"Line is not valid JSON ({ex.Message}): {line}\n{result}");
            }
        }
        return parsed;
    }

    private static async Task CopyLinesAsync(StreamReader reader, StringBuilder target, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (target)
                {
                    _ = target.Append(line).Append('\n');
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }

    private static string LocateExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "devproxy.exe" : "devproxy";

        var configured = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            var path = Directory.Exists(configured) ? Path.Combine(configured, fileName) : configured;
            path = Path.GetFullPath(path);
            return File.Exists(path)
                ? path
                : throw new CliLaunchException($"{ExecutableEnvironmentVariable} points to '{path}', which doesn't exist.");
        }

        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DevProxy", "bin", configuration, "net10.0", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new CliLaunchException(
            $"Couldn't find the devproxy executable. Build DevProxy ({configuration}) or set {ExecutableEnvironmentVariable} to the executable or its folder.");
    }
}

/// <summary>
/// The Dev Proxy executable couldn't be found or started. CI reports a scenario that fails
/// with this exception as inconclusive rather than as a product failure, so keep the type
/// name in sync with <c>.github/scripts/cli-scenarios-on-base.cs</c>.
/// </summary>
sealed class CliLaunchException(string message, Exception? innerException = null) : Exception(message, innerException);
