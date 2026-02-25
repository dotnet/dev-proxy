// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DevProxy.State;

/// <summary>
/// Manages the state file for detached Dev Proxy instances.
/// </summary>
internal static class StateManager
{
    private const string StateFileName = "state.json";
    private const string LogsFolderName = "logs";
    private const string ProxyConfigurationFolderName = "dev-proxy";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Gets the path to the Dev Proxy configuration folder.
    /// On macOS: ~/Library/Application Support/dev-proxy/
    /// On Linux: ~/.config/dev-proxy/ (or $XDG_CONFIG_HOME/dev-proxy/)
    /// On Windows: %LocalAppData%\dev-proxy\
    /// </summary>
    public static string GetConfigFolder()
    {
        string basePath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use LocalApplicationData on Windows for consistency with CertificateDiskCache
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            // Use ApplicationData on macOS/Linux (~/.config)
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        return Path.Combine(basePath, ProxyConfigurationFolderName);
    }

    /// <summary>
    /// Gets the path to the state file.
    /// </summary>
    public static string GetStateFilePath()
    {
        return Path.Combine(GetConfigFolder(), StateFileName);
    }

    /// <summary>
    /// Gets the path to the logs folder.
    /// </summary>
    public static string GetLogsFolder()
    {
        return Path.Combine(GetConfigFolder(), LogsFolderName);
    }

    /// <summary>
    /// Generates a log file path for a new detached instance.
    /// </summary>
    public static string GenerateLogFilePath()
    {
        var logsFolder = GetLogsFolder();
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var pid = Environment.ProcessId;
        return Path.Combine(logsFolder, $"devproxy-{pid}-{timestamp}.log");
    }

    /// <summary>
    /// Saves the state of a running detached instance.
    /// </summary>
    public static async Task SaveStateAsync(ProxyInstanceState state, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath();
        var directory = Path.GetDirectoryName(stateFilePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(stateFilePath, json, cancellationToken);
    }

    /// <summary>
    /// Loads the state of a running detached instance.
    /// Returns null if no state file exists or if the state is stale (process not running).
    /// </summary>
    public static async Task<ProxyInstanceState?> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath();

        if (!File.Exists(stateFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, cancellationToken);
            var state = JsonSerializer.Deserialize<ProxyInstanceState>(json);

            if (state == null)
            {
                return null;
            }

            // Verify the process is still running
            if (!IsProcessRunning(state.Pid))
            {
                // Clean up stale state file
                await DeleteStateAsync(cancellationToken);
                return null;
            }

            return state;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the state file.
    /// </summary>
    public static Task DeleteStateAsync(CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetStateFilePath();

        try
        {
            if (File.Exists(stateFilePath))
            {
                File.Delete(stateFilePath);
            }
        }
        catch (IOException)
        {
            // Ignore - file might be locked or already deleted
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if a detached instance is already running.
    /// </summary>
    public static async Task<bool> IsInstanceRunningAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state != null;
    }

    /// <summary>
    /// Checks if a process with the given PID is running.
    /// </summary>
    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited
            return false;
        }
    }

    /// <summary>
    /// Cleans up old log files, keeping only the most recent ones.
    /// </summary>
    public static void CleanupOldLogs(int maxAgeDays = 7, int maxFiles = 10)
    {
        var logsFolder = GetLogsFolder();

        if (!Directory.Exists(logsFolder))
        {
            return;
        }

        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeDays);
            var logFiles = Directory.GetFiles(logsFolder, "devproxy-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            // Keep the most recent maxFiles
            var filesToDelete = logFiles.Skip(maxFiles)
                .Union(logFiles.Where(f => f.CreationTimeUtc < cutoffDate))
                .Distinct()
                .ToList();

            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // Ignore - file might be in use
                }
            }
        }
        catch (IOException)
        {
            // Ignore cleanup errors
        }
    }
}