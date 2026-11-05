// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DevProxy.Abstractions.Proxy;
using DevProxy.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace DevProxy.Proxy;

/// <summary>
/// The outcome of restoring the OS proxy settings.
/// </summary>
internal enum SystemProxyRestoreResult
{
    /// <summary>The settings captured before Dev Proxy registered itself were put back.</summary>
    RestoredPrevious,
    /// <summary>No previous settings were recorded, so the system proxy was turned off.</summary>
    Disabled,
    /// <summary>Dev Proxy doesn't configure the system proxy on this OS.</summary>
    NotSupported,
    /// <summary>The OS settings couldn't be changed. Details are logged.</summary>
    Failed,
}

/// <summary>
/// Host-side <see cref="ISystemProxyManager"/>. Engine-agnostic OS proxy on/off, shared by
/// the Kestrel engine, the <c>stop --force</c> crash-cleanup path, and
/// <c>devproxy system-proxy reset</c>.
///
/// <code>
///   Enable(ip, port)                 Disable()
///   ────────────────                 ─────────
///   snapshot current settings        restore snapshot when present
///   Windows → registry ProxyServer   Windows → registry ProxyServer/ProxyEnable
///             + ProxyEnable = 1                (or ProxyEnable = 0) + WinINET refresh
///             + WinINET refresh
///   macOS   → toggle-proxy.sh on …   macOS   → toggle-proxy.sh off + networksetup
///   Linux   → log warning            Linux   → no-op
/// </code>
///
/// <para>
/// Before registering, the current OS proxy settings are written to a snapshot file
/// (<see cref="SystemProxySnapshotStore"/>) together with the owning PID. Restoring reads
/// that file, so the previous settings can be put back even after the owning instance was
/// killed or crashed.
/// </para>
///
/// <para>
/// The Windows path uses WinINET: it writes the per-user Internet Settings registry values
/// and then broadcasts INTERNET_OPTION_SETTINGS_CHANGED + INTERNET_OPTION_REFRESH so running
/// applications re-read the proxy without a restart. This is the standard WinINET mechanism;
/// it cannot be exercised on non-Windows hosts.
/// </para>
/// </summary>
internal sealed class SystemProxyManager : ISystemProxyManager
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private readonly ILogger<SystemProxyManager> _logger;
    private readonly string _snapshotPath;
    private bool _savedSnapshot;

    public SystemProxyManager(ILogger<SystemProxyManager> logger) :
        this(logger, SystemProxySnapshotStore.DefaultPath)
    {
    }

    internal SystemProxyManager(ILogger<SystemProxyManager> logger, string snapshotPath)
    {
        _logger = logger;
        _snapshotPath = snapshotPath;
    }

    public void Enable(string? ipAddress, int port)
    {
        if (OperatingSystem.IsWindows())
        {
            SaveSnapshot(CaptureWindows);
            EnableWindows(SystemProxyAddress.ToHostPort(ipAddress, port));
        }
        else if (OperatingSystem.IsMacOS())
        {
            SaveSnapshot(CaptureMacOS);
            _ = RunToggleScript(
                "on",
                SystemProxyAddress.ResolveHost(ipAddress),
                port.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            _logger.LogWarning(
                "Configure your operating system to use this proxy's port and address {Address}:{Port}",
                SystemProxyAddress.ResolveHost(ipAddress),
                port);
        }
    }

    public void Disable()
    {
        if (_savedSnapshot)
        {
            _savedSnapshot = false;
            // The snapshot is gone or belongs to another process when 'system-proxy reset'
            // already restored the settings or another instance took over. Restoring now
            // would undo that, so leave the settings alone.
            if (SystemProxySnapshotStore.Load(_snapshotPath)?.Pid != Environment.ProcessId)
            {
                return;
            }
        }

        _ = Restore();
    }

    /// <summary>
    /// Puts back the OS proxy settings recorded before Dev Proxy registered itself, or turns
    /// the system proxy off when nothing was recorded. Safe to call when Dev Proxy never
    /// changed the settings.
    /// </summary>
    internal SystemProxyRestoreResult Restore()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return SystemProxyRestoreResult.NotSupported;
        }

        var snapshot = SystemProxySnapshotStore.Load(_snapshotPath);
        bool succeeded;
        bool restoredPrevious;
        if (OperatingSystem.IsWindows())
        {
            restoredPrevious = snapshot?.Windows is not null;
            succeeded = restoredPrevious
                ? RestoreWindows(snapshot!.Windows!)
                : DisableWindows();
        }
        else
        {
            succeeded = RunToggleScript("off");
            restoredPrevious = snapshot?.MacOS is not null;
            if (succeeded && restoredPrevious)
            {
                succeeded = RestoreMacOS(snapshot!.MacOS!);
            }
        }

        if (!succeeded)
        {
            // Keep the snapshot so a later reset can still put the previous settings back.
            return SystemProxyRestoreResult.Failed;
        }

        if (snapshot is not null)
        {
            SystemProxySnapshotStore.Delete(_snapshotPath);
        }
        return restoredPrevious
            ? SystemProxyRestoreResult.RestoredPrevious
            : SystemProxyRestoreResult.Disabled;
    }

    private void SaveSnapshot(Action<SystemProxySnapshot> capture)
    {
        try
        {
            // Keep an existing snapshot: it holds the settings from before Dev Proxy first
            // took over, while the current settings may already point at Dev Proxy.
            var snapshot = SystemProxySnapshotStore.Load(_snapshotPath);
            if (snapshot is null)
            {
                snapshot = new() { CreatedAt = DateTimeOffset.UtcNow };
                capture(snapshot);
            }
            snapshot.Pid = Environment.ProcessId;
            SystemProxySnapshotStore.Save(_snapshotPath, snapshot);
            _savedSnapshot = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Couldn't save the current system proxy settings. They can't be restored when Dev Proxy stops.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CaptureWindows(SystemProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        snapshot.Windows = new()
        {
            ProxyEnable = key?.GetValue("ProxyEnable") is int enable ? enable : null,
            ProxyServer = key?.GetValue("ProxyServer") as string,
        };
    }

    [SupportedOSPlatform("windows")]
    private void EnableWindows(string hostPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                _logger.LogError("Could not open the Windows Internet Settings registry key.");
                return;
            }

            key.SetValue("ProxyServer", hostPort, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            NotifyWinInetSettingsChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogError(ex, "Failed to set the system proxy via the Windows registry.");
        }
    }

    [SupportedOSPlatform("windows")]
    private bool DisableWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifyWinInetSettingsChanged();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogError(ex, "Failed to clear the system proxy via the Windows registry.");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool RestoreWindows(WindowsProxySettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (settings.ProxyServer is null)
            {
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ProxyServer", settings.ProxyServer, RegistryValueKind.String);
            }

            if (settings.ProxyEnable is null)
            {
                key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ProxyEnable", settings.ProxyEnable.Value, RegistryValueKind.DWord);
            }

            NotifyWinInetSettingsChanged();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogError(ex, "Failed to restore the system proxy via the Windows registry.");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyWinInetSettingsChanged()
    {
        // Tell WinINET-based applications to re-read proxy settings without a restart.
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    // DllImport (not LibraryImport) is used deliberately: LibraryImport's source generator
    // requires AllowUnsafeBlocks project-wide, which we avoid for this single system call.
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
#pragma warning restore SYSLIB1054

    private static void CaptureMacOS(SystemProxySnapshot snapshot)
    {
        var services = MacOSNetworkSetup.ParseNetworkServices(
            RunNetworkSetup("-listallnetworkservices"));
        snapshot.MacOS = [.. services.Select(service => new MacOSServiceProxySettings
        {
            Service = service,
            Web = MacOSNetworkSetup.ParseProxyEndpoint(RunNetworkSetup("-getwebproxy", service)),
            SecureWeb = MacOSNetworkSetup.ParseProxyEndpoint(RunNetworkSetup("-getsecurewebproxy", service)),
        })];
    }

    private bool RestoreMacOS(IEnumerable<MacOSServiceProxySettings> services)
    {
        try
        {
            foreach (var command in services.SelectMany(MacOSNetworkSetup.GetRestoreCommands))
            {
                _ = RunNetworkSetup(command);
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Failed to restore the system proxy via networksetup.");
            return false;
        }
    }

    private static string RunNetworkSetup(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = MacOSNetworkSetup.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _ = output.AppendLine(e.Data);
            }
        };
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill();
            throw new InvalidOperationException($"networksetup {arguments[0]} timed out.");
        }
        // Flush the asynchronous output readers.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"networksetup {arguments[0]} exited with code {process.ExitCode}.");
        }
        return output.ToString();
    }

    private bool RunToggleScript(params string[] arguments)
    {
        var bashScriptPath = Path.Join(AppContext.BaseDirectory, "toggle-proxy.sh");
        if (!File.Exists(bashScriptPath))
        {
            _logger.LogWarning("Could not find {Script} to toggle the system proxy.", "toggle-proxy.sh");
            return false;
        }

        var startInfo = CreateToggleScriptStartInfo(bashScriptPath, arguments);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            _ = process.Start();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                _logger.LogError("Timed out toggling the system proxy via toggle-proxy.sh.");
                return false;
            }
            if (process.ExitCode != 0)
            {
                _logger.LogError("toggle-proxy.sh exited with code {ExitCode}.", process.ExitCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Failed to toggle the system proxy via toggle-proxy.sh.");
            return false;
        }
    }

    internal static ProcessStartInfo CreateToggleScriptStartInfo(string bashScriptPath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(bashScriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    // ------------------------------------------------------------------
    // Static helpers for orphan reconciliation (crash-cleanup path).
    // ------------------------------------------------------------------

    /// <summary>
    /// The outcome of reconciling orphaned system-proxy registrations.
    /// </summary>
    /// <param name="OrphanedPids">PIDs of dead instances that left a system-proxy registration behind.</param>
    /// <param name="SystemProxyRestored">Whether the OS proxy settings were restored.</param>
    internal readonly record struct OrphanReconciliation(
        IReadOnlyList<int> OrphanedPids,
        bool SystemProxyRestored);

    /// <summary>
    /// Finds the PID of a live process that owns the system proxy: a running detached
    /// instance, or the (foreground or detached) instance recorded in the snapshot file.
    /// Returns null when no live process owns it.
    /// </summary>
    internal static async Task<int?> FindLiveSystemProxyOwnerAsync(CancellationToken cancellationToken = default)
    {
        var liveInstance = await StateManager.FindSystemProxyInstanceAsync(cancellationToken);
        if (liveInstance is not null)
        {
            return liveInstance.Pid;
        }

        var snapshot = SystemProxySnapshotStore.Load(SystemProxySnapshotStore.DefaultPath);
        return snapshot is not null && StateManager.IsProcessRunning(snapshot.Pid)
            ? snapshot.Pid
            : null;
    }

    /// <summary>
    /// Reconciles system-proxy registrations left behind by crashed or killed instances.
    /// Restores the OS proxy (unless a live instance still owns it) and removes
    /// the stale state records. Safe to call when there are no orphans.
    /// </summary>
    public static async Task<OrphanReconciliation> ReconcileOrphanedSystemProxiesAsync(CancellationToken cancellationToken = default)
    {
        var orphanedPids = (await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
            .Select(o => o.Pid)
            .ToList();

        var snapshot = SystemProxySnapshotStore.Load(SystemProxySnapshotStore.DefaultPath);
        if (snapshot is not null && !StateManager.IsProcessRunning(snapshot.Pid) && !orphanedPids.Contains(snapshot.Pid))
        {
            orphanedPids.Add(snapshot.Pid);
        }

        if (orphanedPids.Count == 0)
        {
            return new([], false);
        }

        var restored = false;
        if (await FindLiveSystemProxyOwnerAsync(cancellationToken) is null)
        {
            restored = new SystemProxyManager(NullLogger<SystemProxyManager>.Instance).Restore()
                is SystemProxyRestoreResult.RestoredPrevious or SystemProxyRestoreResult.Disabled;
        }

        foreach (var pid in orphanedPids)
        {
            await StateManager.DeleteStateAsync(pid, cancellationToken);
        }

        return new(orphanedPids, restored);
    }
}
