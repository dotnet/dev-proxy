// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DevProxy.State;

/// <summary>
/// Manages per-instance state files for detached Dev Proxy instances.
/// Each instance stores its state in a separate file keyed by PID
/// (e.g. state-1234.json) to avoid cross-instance interference.
/// </summary>
internal static class StateManager
{
    private const string LegacyStateFileName = "state.json";
    private const string StateFilePrefix = "state-";
    private const string StateFileExtension = ".json";
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
    /// Gets the per-instance state file path for the given PID.
    /// </summary>
    public static string GetInstanceStateFilePath(int pid)
    {
        return Path.Combine(GetConfigFolder(), $"{StateFilePrefix}{pid}{StateFileExtension}");
    }

    /// <summary>
    /// Gets the path to the legacy single state file (for backward compatibility).
    /// </summary>
    public static string GetStateFilePath()
    {
        return Path.Combine(GetConfigFolder(), LegacyStateFileName);
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
    /// Saves the state of a running detached instance to a per-PID state file.
    /// </summary>
    public static async Task SaveStateAsync(ProxyInstanceState state, CancellationToken cancellationToken = default)
    {
        var stateFilePath = GetInstanceStateFilePath(state.Pid);
        var directory = Path.GetDirectoryName(stateFilePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(stateFilePath, json, cancellationToken);
    }

    /// <summary>
    /// Loads the primary running instance's state.
    /// Prefers the system-proxy instance, then falls back to the most recently
    /// started instance. Returns null if no live instance is found.
    /// </summary>
    public static async Task<ProxyInstanceState?> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        var states = await LoadAllStatesAsync(cancellationToken);
        return states
            .OrderByDescending(s => s.AsSystemProxy)
            .ThenByDescending(s => s.StartedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Loads the state of a specific instance by PID.
    /// Returns null if no state file exists or the process is no longer running.
    /// </summary>
    public static async Task<ProxyInstanceState?> LoadStateByPidAsync(int pid, CancellationToken cancellationToken = default)
    {
        return await LoadStateFromFileAsync(GetInstanceStateFilePath(pid), cancellationToken);
    }

    /// <summary>
    /// Loads all live detached instance states, cleaning up stale state files.
    /// Also checks the legacy state.json for backward compatibility.
    /// </summary>
    public static async Task<List<ProxyInstanceState>> LoadAllStatesAsync(CancellationToken cancellationToken = default)
    {
        var configFolder = GetConfigFolder();
        if (!Directory.Exists(configFolder))
        {
            return [];
        }

        var states = new List<ProxyInstanceState>();
        var seenPids = new HashSet<int>();

        // Check per-instance state files
        var stateFiles = Directory.GetFiles(configFolder, $"{StateFilePrefix}*{StateFileExtension}");
        foreach (var filePath in stateFiles)
        {
            var state = await LoadStateFromFileAsync(filePath, cancellationToken);
            if (state is not null && seenPids.Add(state.Pid))
            {
                states.Add(state);
            }
        }

        // Also check legacy state file for backward compatibility
        var legacyPath = GetStateFilePath();
        if (File.Exists(legacyPath))
        {
            var legacyState = await LoadStateFromFileAsync(legacyPath, cancellationToken);
            if (legacyState is not null && seenPids.Add(legacyState.Pid))
            {
                states.Add(legacyState);
            }
        }

        return states;
    }

    /// <summary>
    /// Finds a running detached instance that owns the system proxy.
    /// Returns null if no system-proxy instance is running.
    /// </summary>
    public static async Task<ProxyInstanceState?> FindSystemProxyInstanceAsync(CancellationToken cancellationToken = default)
    {
        var states = await LoadAllStatesAsync(cancellationToken);
        return states.Find(s => s.AsSystemProxy);
    }

    /// <summary>
    /// Deletes the state file for a specific PID.
    /// Also cleans up the legacy state file if it belongs to this PID.
    /// </summary>
    public static async Task DeleteStateAsync(int pid, CancellationToken cancellationToken = default)
    {
        DeleteFile(GetInstanceStateFilePath(pid));

        // Also clean up legacy state file if it belongs to this PID
        var legacyPath = GetStateFilePath();
        try
        {
            if (File.Exists(legacyPath))
            {
                var json = await File.ReadAllTextAsync(legacyPath, cancellationToken);
                var state = JsonSerializer.Deserialize<ProxyInstanceState>(json);
                if (state?.Pid == pid)
                {
                    DeleteFile(legacyPath);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore corrupt legacy file
        }
        catch (IOException)
        {
            // Ignore I/O errors
        }
    }

    /// <summary>
    /// Deletes the state file for the current process.
    /// </summary>
    public static Task DeleteStateAsync(CancellationToken cancellationToken = default)
    {
        return DeleteStateAsync(Environment.ProcessId, cancellationToken);
    }

    /// <summary>
    /// Checks if a detached instance is already running.
    /// </summary>
    public static async Task<bool> IsInstanceRunningAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state is not null;
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
    /// Loads a state from a specific file path.
    /// Returns null if the file doesn't exist, can't be parsed,
    /// or refers to a process that's no longer running.
    /// Cleans up the file if the process is stale.
    /// </summary>
    private static async Task<ProxyInstanceState?> LoadStateFromFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var state = JsonSerializer.Deserialize<ProxyInstanceState>(json);

            if (state is null)
            {
                return null;
            }

            // Verify the process is still running
            if (!IsProcessRunning(state.Pid))
            {
                // Clean up stale state file
                DeleteFile(filePath);
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

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore - file might be locked or already deleted
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