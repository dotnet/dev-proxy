// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using DevProxy.State;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Keep management files discoverable even when env overrides change user data paths.
_ = StateManager.GetConfigFolder();

// Handle detached mode - spawn a child process and exit
// Only applies to root command (starting the proxy), not subcommands
if (DevProxyCommand.IsRootCommand &&
    DevProxyCommand.IsDetachedMode &&
    !DevProxyCommand.IsInternalDaemon)
{
    var detachedExitCode = await StartDetachedProcessAsync(args);
    Environment.Exit(detachedExitCode);
    return;
}

// For daemon mode, redirect Console.Out/Error to the log file
// so that the normal console formatters (ProxyConsoleFormatter,
// MachineConsoleFormatter) write to the file instead of stdout.
// Console.IsOutputRedirected becomes true, which automatically
// strips ANSI color codes from human-readable output.
StreamWriter? _detachedLogWriter = null;
if (DevProxyCommand.IsInternalDaemon)
{
    var logFilePath = DevProxyCommand.DetachedLogFilePath;
    var logDir = Path.GetDirectoryName(logFilePath);
    if (!string.IsNullOrEmpty(logDir))
    {
        PrivateFiles.EnsureDirectory(StateManager.GetConfigFolder());
        PrivateFiles.EnsureDirectory(logDir);
    }

#pragma warning disable CA2000 // Lifetime managed manually; disposed before Environment.Exit
    _detachedLogWriter = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
#pragma warning restore CA2000
    Console.SetOut(_detachedLogWriter);
    Console.SetError(_detachedLogWriter);
}

static async Task<int> StartDetachedProcessAsync(string[] args)
{
    var isJsonOutput = IsJsonOutputRequested(args);

    // Recover from a system-proxy registration left behind by a previous
    // instance that crashed before restoring the OS proxy. This must run before
    // any state-loading call below, which prunes (deletes) the stale state files
    // it depends on without restoring the proxy.
    var reconciliation = await SystemProxyManager.ReconcileOrphanedSystemProxiesAsync();
    foreach (var orphan in reconciliation.Orphans)
    {
        var message = reconciliation.SystemProxyDisabled
            ? $"Recovered system proxy left by a crashed Dev Proxy instance (PID: {orphan.Pid})."
            : $"Removed stale system-proxy registration left by a crashed Dev Proxy instance (PID: {orphan.Pid}) while keeping the current system proxy owner unchanged.";
        if (isJsonOutput)
        {
            await Console.Out.WriteLineAsync(FormatJsonLogEntry("info", message));
        }
        else
        {
            await Console.Out.WriteLineAsync(message);
        }
    }

    // Check if an instance is already running as system proxy
    var systemProxyInstance = await StateManager.FindSystemProxyInstanceAsync();
    if (systemProxyInstance is not null)
    {
        if (isJsonOutput)
        {
            await Console.Error.WriteLineAsync(
                FormatJsonLogEntry("error", $"Dev Proxy is already running as system proxy (PID: {systemProxyInstance.Pid}). Use 'devproxy stop' to stop it first."));
        }
        else
        {
            await Console.Error.WriteLineAsync($"Dev Proxy is already running as system proxy (PID: {systemProxyInstance.Pid}).");
            await Console.Error.WriteLineAsync("Use 'devproxy stop' to stop it first.");
        }
        return 1;
    }

    // Clean up old log files
    StateManager.CleanupOldLogs();

    // Build the arguments for the daemon process
    // Replace --detach/-d with --_internal-daemon
    var daemonArgs = args
        .Where(a => a is not "--detach" and not "-d")
        .Append("--_internal-daemon")
        .ToArray();

    var executablePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(executablePath))
    {
        if (isJsonOutput)
        {
            await Console.Error.WriteLineAsync(
                FormatJsonLogEntry("error", "Could not determine executable path."));
        }
        else
        {
            await Console.Error.WriteLineAsync("Could not determine executable path.");
        }
        return 1;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        Arguments = string.Join(" ", daemonArgs.Select(EscapeArgument)),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Directory.GetCurrentDirectory()
    };

    // On Windows, we need to detach from the console
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    }

    try
    {
        var process = Process.Start(startInfo);
        if (process == null)
        {
            if (isJsonOutput)
            {
                await Console.Error.WriteLineAsync(
                    FormatJsonLogEntry("error", "Failed to start Dev Proxy process."));
            }
            else
            {
                await Console.Error.WriteLineAsync("Failed to start Dev Proxy process.");
            }
            return 1;
        }

        // Wait a moment for the proxy to start and write its state file
        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(200);

            var state = await StateManager.LoadStateByPidAsync(process.Id);
            if (state is { Port: > 0 } && !string.IsNullOrEmpty(state.ApiUrl) && !state.ApiUrl.EndsWith(":0", StringComparison.Ordinal))
            {
                var proxyUrl = state.ProxyUrl;
                var token = ApiSecurity.ShouldDisplayTokenFor(state) ?
                    (await File.ReadAllTextAsync(ApiSecurity.GetTokenFilePath(state.Pid))).Trim() : null;

                if (isJsonOutput)
                {
                    await Console.Out.WriteLineAsync(FormatJsonResultEntry(new
                    {
                        state.Pid,
                        ProxyUrl = proxyUrl,
                        state.ApiUrl,
                        Token = token,
                        state.LogFile
                    }));
                }
                else
                {
                    var showToken = token is not null;
                    await Console.Out.WriteLineAsync("Dev Proxy started in background.");
                    await Console.Out.WriteLineAsync();
                    await Console.Out.WriteLineAsync($"  PID:       {state.Pid}");
                    await Console.Out.WriteLineAsync($"  Proxy URL: {proxyUrl}");
                    await Console.Out.WriteLineAsync($"  API URL:   {state.ApiUrl}");
                    if (showToken)
                    {
                        await Console.Out.WriteLineAsync($"  API token: {token}");
                    }
                    await Console.Out.WriteLineAsync($"  Log file:  {state.LogFile}");
                    await Console.Out.WriteLineAsync();
                    if (!showToken)
                    {
                        await Console.Out.WriteLineAsync($"Use 'devproxy api token --pid {state.Pid}' to get the API token.");
                    }
                    await Console.Out.WriteLineAsync("Use 'devproxy status' to check status.");
                    await Console.Out.WriteLineAsync("Use 'devproxy logs' to view logs.");
                    await Console.Out.WriteLineAsync("Use 'devproxy stop' to stop.");
                }
                return 0;
            }

            // Check if process exited prematurely
            if (process.HasExited)
            {
                var errorOutput = await process.StandardError.ReadToEndAsync();
                var standardOutput = await process.StandardOutput.ReadToEndAsync();
                // The daemon redirects its console to its log file, so that's where
                // the reason for the failure (like a port conflict) ends up.
                var logOutput = await ReadDaemonLogAsync(process.Id);

                if (isJsonOutput)
                {
                    if (!string.IsNullOrEmpty(logOutput))
                    {
                        foreach (var entry in ToJsonLogEntries(logOutput))
                        {
                            await Console.Error.WriteLineAsync(entry);
                        }
                    }
                    var message = "Dev Proxy failed to start.";
                    if (!string.IsNullOrEmpty(errorOutput))
                    {
                        message += $" {errorOutput.Trim()}";
                    }
                    if (!string.IsNullOrEmpty(standardOutput))
                    {
                        message += $" {standardOutput.Trim()}";
                    }
                    await Console.Error.WriteLineAsync(FormatJsonLogEntry("error", message));
                }
                else
                {
                    await Console.Error.WriteLineAsync("Dev Proxy failed to start.");
                    if (!string.IsNullOrEmpty(logOutput))
                    {
                        await Console.Error.WriteLineAsync(logOutput.TrimEnd());
                    }
                    if (!string.IsNullOrEmpty(errorOutput))
                    {
                        await Console.Error.WriteLineAsync(errorOutput);
                    }
                    if (!string.IsNullOrEmpty(standardOutput))
                    {
                        await Console.Error.WriteLineAsync(standardOutput);
                    }
                }
                return process.ExitCode;
            }
        }

        if (isJsonOutput)
        {
            await Console.Error.WriteLineAsync(
                FormatJsonLogEntry("error", $"Timeout waiting for Dev Proxy to start. Check the log folder: {StateManager.GetLogsFolder()}"));
        }
        else
        {
            await Console.Error.WriteLineAsync("Timeout waiting for Dev Proxy to start.");
            await Console.Error.WriteLineAsync($"Check the log folder: {StateManager.GetLogsFolder()}");
        }
        return 1;
    }
    catch (Exception ex)
    {
        if (isJsonOutput)
        {
            await Console.Error.WriteLineAsync(
                FormatJsonLogEntry("error", $"Failed to start Dev Proxy: {ex.Message}"));
        }
        else
        {
            await Console.Error.WriteLineAsync($"Failed to start Dev Proxy: {ex.Message}");
        }
        return 1;
    }
}

/// <summary>
/// Formats a result entry matching the JSONL envelope produced by JsonConsoleFormatter:
/// {"type":"result","data":{...},"timestamp":"..."}
/// </summary>
static string FormatJsonResultEntry(object data)
{
#pragma warning disable CA1869 // Called at most once per process lifetime
    var jsonlOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
#pragma warning restore CA1869
    var entry = new
    {
        type = "result",
        data = JsonSerializer.SerializeToElement(data, jsonlOptions),
        timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };
    return JsonSerializer.Serialize(entry, jsonlOptions);
}

/// <summary>
/// Formats a log entry matching the JSONL envelope produced by JsonConsoleFormatter:
/// {"type":"log","level":"...","message":"...","timestamp":"..."}
/// </summary>
static string FormatJsonLogEntry(string level, string message)
{
#pragma warning disable CA1869 // Called at most once per process lifetime
    var jsonlOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
#pragma warning restore CA1869
    var entry = new
    {
        type = "log",
        level,
        message,
        timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };
    return JsonSerializer.Serialize(entry, jsonlOptions);
}

// The daemon logs JSONL, but output it doesn't control (like unhandled
// exception stack traces) can end up in its log too. Keep JSON entries as-is
// and wrap other runs of lines so the output stays valid JSONL.
static IEnumerable<string> ToJsonLogEntries(string log)
{
    var pending = new List<string>();
    foreach (var line in log.TrimEnd().Split('\n'))
    {
        var trimmed = line.TrimEnd('\r');
        if (IsJsonObject(trimmed))
        {
            if (pending.Count > 0)
            {
                yield return FormatJsonLogEntry("error", string.Join(Environment.NewLine, pending));
                pending.Clear();
            }
            yield return trimmed;
        }
        else if (pending.Count > 0 || !string.IsNullOrWhiteSpace(trimmed))
        {
            pending.Add(trimmed);
        }
    }
    if (pending.Count > 0)
    {
        yield return FormatJsonLogEntry("error", string.Join(Environment.NewLine, pending).TrimEnd());
    }
}

static bool IsJsonObject(string line)
{
    if (!line.StartsWith('{'))
    {
        return false;
    }
    try
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }
    catch (JsonException)
    {
        return false;
    }
}

static async Task<string?> ReadDaemonLogAsync(int pid)
{
    try
    {
        var logsFolder = StateManager.GetLogsFolder();
        if (!Directory.Exists(logsFolder))
        {
            return null;
        }
        var logFile = Directory.GetFiles(logsFolder, $"devproxy-{pid}-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return logFile is null ? null : await File.ReadAllTextAsync(logFile);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

static bool IsJsonOutputRequested(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], DevProxyCommand.OutputOptionName, StringComparison.OrdinalIgnoreCase) &&
            i + 1 < args.Length)
        {
            return string.Equals(args[i + 1], "json", StringComparison.OrdinalIgnoreCase);
        }
    }
    return false;
}

static string EscapeArgument(string arg)
{
    // Simple escaping for command line arguments
    if (arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('"', StringComparison.Ordinal))
    {
        return $"\"{arg.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
    return arg;
}

static WebApplication BuildApplication(
    DevProxyConfigOptions options, ProcessEnvironment environment, HttpProxyEnvironment proxyEnvironment)
{
    // Don't pass command-line args to WebApplication.CreateBuilder because:
    // 1. Dev Proxy uses System.CommandLine for CLI parsing, not ASP.NET Core's CommandLineConfigurationProvider
    // 2. ConfigureDevProxyConfig clears all configuration sources anyway and only uses JSON config files
    var builder = WebApplication.CreateBuilder();

    _ = builder.Configuration.ConfigureDevProxyConfig(options);
    environment.Apply(builder.Configuration, options.Env);
    var changedProxyVariables = proxyEnvironment.GetChangedVariables();
    _ = Announcement.ShowOnceAsync();
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
    var allowedOrigins = ApiSecurity.GetAllowedOrigins(builder.Configuration);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options, allowedOrigins);

    var defaultApiPort = 8897;
    var apiPort = options.ApiPort ??
        builder.Configuration.GetValue("apiPort", defaultApiPort);
    var apiAddressText = options.ApiIpAddress ?? builder.Configuration.GetValue("apiIpAddress", "127.0.0.1");
    if (!IPAddress.TryParse(apiAddressText, out var apiAddress))
    {
        throw new InvalidOperationException("apiIpAddress must be an IP address, for example 127.0.0.1 or ::1.");
    }
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(apiAddress, apiPort);
    });

    var app = builder.Build();

    if (changedProxyVariables.Length > 0)
    {
        app.Logger.LogWarning(
            "Proxy-routing environment variables changed ({EnvironmentVariables}). HTTP proxy settings are cached and cannot be refreshed by hot reload. Stop Dev Proxy and start it again to apply these changes.",
            string.Join(", ", changedProxyVariables));
    }

    _ = app.Use((context, next) => ApiSecurity.CheckOriginAsync(context, next, allowedOrigins));
    _ = app.UseCors();
    _ = app.Use(ApiSecurity.AuthenticateAsync);
    _ = app.MapControllers();

    return app;
}

static async Task<int> RunProxyAsync(string[] args, DevProxyConfigOptions options, HttpProxyEnvironment proxyEnvironment)
{
    // Keep overrides through host shutdown, but restore inherited values before a hot reload.
    using var environment = new ProcessEnvironment();
    var app = BuildApplication(options, environment, proxyEnvironment);
    try
    {
        var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
        return await devProxyCommand.InvokeAsync(args, app);
    }
    finally
    {
        await app.DisposeAsync();
    }
}

// Handle 'config validate' early to allow validation of broken configs
// without requiring the full DI container to be built
if (DevProxyCommand.IsConfigValidateCommand)
{
    _ = Announcement.ShowOnceAsync();
    var validateExitCode = await ConfigCommand.RunValidateStandaloneAsync(args);
    Environment.Exit(validateExitCode);
    return;
}

var options = new DevProxyConfigOptions();
options.ParseOptions(args);

int exitCode;
bool shouldRestart;
var proxyEnvironment = new HttpProxyEnvironment();
do
{
    try
    {
        // Reset the restart flag before each run
        ConfigFileWatcher.Reset();
        // RunProxyAsync only returns after the host has fully stopped, which
        // means the proxy engine's shutdown (including system proxy
        // deregistration) has completed — so it's safe to start a new instance.
        exitCode = await RunProxyAsync(args, options, proxyEnvironment);

        shouldRestart = ConfigFileWatcher.IsRestarting;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync("Unhandled exception during proxy run. Stopping restart loop.");
        await Console.Error.WriteLineAsync(ex.ToString());
        exitCode = 1;
        shouldRestart = false;
    }
} while (shouldRestart);

if (DevProxyCommand.IsRootCommand)
{
    await StateManager.DeleteStateAsync();
}

if (_detachedLogWriter is not null)
{
    await _detachedLogWriter.DisposeAsync();
}

Environment.Exit(exitCode);