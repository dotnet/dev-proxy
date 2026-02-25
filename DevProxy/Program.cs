// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy;
using DevProxy.Commands;
using DevProxy.Proxy;
using DevProxy.State;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

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
    if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
    {
        _ = Directory.CreateDirectory(logDir);
    }

#pragma warning disable CA2000 // Lifetime managed manually; disposed before Environment.Exit
    _detachedLogWriter = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
#pragma warning restore CA2000
    Console.SetOut(_detachedLogWriter);
    Console.SetError(_detachedLogWriter);
}

static async Task<int> StartDetachedProcessAsync(string[] args)
{
    // Check if an instance is already running
    if (await StateManager.IsInstanceRunningAsync())
    {
        var existingState = await StateManager.LoadStateAsync();
        await Console.Error.WriteLineAsync($"Dev Proxy is already running (PID: {existingState?.Pid}).");
        await Console.Error.WriteLineAsync("Use 'devproxy stop' to stop it first.");
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
        await Console.Error.WriteLineAsync("Could not determine executable path.");
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
            await Console.Error.WriteLineAsync("Failed to start Dev Proxy process.");
            return 1;
        }

        // Wait a moment for the proxy to start and write its state file
        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(200);

            var state = await StateManager.LoadStateAsync();
            if (state != null)
            {
                await Console.Out.WriteLineAsync("Dev Proxy started in background.");
                await Console.Out.WriteLineAsync();
                await Console.Out.WriteLineAsync($"  PID:       {state.Pid}");
                await Console.Out.WriteLineAsync($"  API URL:   {state.ApiUrl}");
                await Console.Out.WriteLineAsync($"  Log file:  {state.LogFile}");
                await Console.Out.WriteLineAsync();
                await Console.Out.WriteLineAsync("Use 'devproxy status' to check status.");
                await Console.Out.WriteLineAsync("Use 'devproxy logs' to view logs.");
                await Console.Out.WriteLineAsync("Use 'devproxy stop' to stop.");
                return 0;
            }

            // Check if process exited prematurely
            if (process.HasExited)
            {
                var errorOutput = await process.StandardError.ReadToEndAsync();
                var standardOutput = await process.StandardOutput.ReadToEndAsync();

                await Console.Error.WriteLineAsync("Dev Proxy failed to start.");
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    await Console.Error.WriteLineAsync(errorOutput);
                }
                if (!string.IsNullOrEmpty(standardOutput))
                {
                    await Console.Error.WriteLineAsync(standardOutput);
                }
                return process.ExitCode;
            }
        }

        await Console.Error.WriteLineAsync("Timeout waiting for Dev Proxy to start.");
        await Console.Error.WriteLineAsync($"Check the log folder: {StateManager.GetLogsFolder()}");
        return 1;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"Failed to start Dev Proxy: {ex.Message}");
        return 1;
    }
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

static WebApplication BuildApplication(DevProxyConfigOptions options)
{
    // Don't pass command-line args to WebApplication.CreateBuilder because:
    // 1. Dev Proxy uses System.CommandLine for CLI parsing, not ASP.NET Core's CommandLineConfigurationProvider
    // 2. ConfigureDevProxyConfig clears all configuration sources anyway and only uses JSON config files
    var builder = WebApplication.CreateBuilder();

    _ = builder.Configuration.ConfigureDevProxyConfig(options);
    _ = builder.Logging.ConfigureDevProxyLogging(builder.Configuration, options);
    _ = builder.Services.ConfigureDevProxyServices(builder.Configuration, options);

    var defaultIpAddress = "127.0.0.1";
    var ipAddress = options.IPAddress ??
        builder.Configuration.GetValue("ipAddress", defaultIpAddress) ??
        defaultIpAddress;
    var defaultApiPort = 8897;
    var apiPort = options.ApiPort ??
        builder.Configuration.GetValue("apiPort", defaultApiPort);
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Parse(ipAddress), apiPort);
    });

    var app = builder.Build();

    _ = app.UseCors();
    _ = app.UseSwagger();
    _ = app.UseSwaggerUI();
    _ = app.MapControllers();

    return app;
}

static async Task<int> RunProxyAsync(string[] args, DevProxyConfigOptions options)
{
    var app = BuildApplication(options);
    try
    {
        // If running as daemon, save state so other commands can find us
        if (DevProxyCommand.IsInternalDaemon)
        {
            var ipAddress = options.IPAddress ?? app.Configuration.GetValue("ipAddress", "127.0.0.1") ?? "127.0.0.1";
            var apiPort = options.ApiPort ?? app.Configuration.GetValue("apiPort", 8897);
            var port = options.Port ?? app.Configuration.GetValue("port", 8000);

            var state = new ProxyInstanceState
            {
                Pid = Environment.ProcessId,
                ApiUrl = $"http://{(ipAddress is "0.0.0.0" or "::" ? "127.0.0.1" : ipAddress)}:{apiPort}",
                LogFile = DevProxyCommand.DetachedLogFilePath,
                StartedAt = DateTimeOffset.UtcNow,
                ConfigFile = options.ConfigFile,
                Port = port
            };

            await StateManager.SaveStateAsync(state);
        }

        var devProxyCommand = app.Services.GetRequiredService<DevProxyCommand>();
        return await devProxyCommand.InvokeAsync(args, app);
    }
    finally
    {
        // Clean up state file when daemon exits
        if (DevProxyCommand.IsInternalDaemon)
        {
            await StateManager.DeleteStateAsync();
        }

        // Dispose the app to clean up all services (including FileSystemWatchers in BaseLoader)
        await app.DisposeAsync();
    }
}

_ = Announcement.ShowAsync();

var options = new DevProxyConfigOptions();
options.ParseOptions(args);

int exitCode;
bool shouldRestart;
do
{
    try
    {
        // Reset the restart flag before each run
        ConfigFileWatcher.Reset();
        exitCode = await RunProxyAsync(args, options);

        // Wait for proxy to fully stop (including system proxy deregistration)
        // before starting the new instance
        if (ConfigFileWatcher.ProxyStoppedCompletionSource is not null)
        {
            var proxyStoppedTask = ConfigFileWatcher.ProxyStoppedCompletionSource.Task;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
#pragma warning disable VSTHRD003 // Intentionally waiting for external signal
            var completedTask = await Task.WhenAny(proxyStoppedTask, timeoutTask);
#pragma warning restore VSTHRD003

            // If the timeout elapses before the proxy signals it has stopped,
            // continue to avoid hanging the restart loop indefinitely
            if (completedTask == proxyStoppedTask)
            {
#pragma warning disable VSTHRD003 // Observe exceptions from completed task
                await proxyStoppedTask;
#pragma warning restore VSTHRD003
            }
        }

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

if (_detachedLogWriter is not null)
{
    await _detachedLogWriter.DisposeAsync();
}

Environment.Exit(exitCode);