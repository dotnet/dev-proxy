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
///   macOS   → toggle-proxy.sh on …   macOS   → networksetup for the recorded services
///                                              (toggle-proxy.sh off without a snapshot)
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

    public void Disable() => Disable(() => _ = Restore());

    internal void Disable(Action restore)
    {
        if (_savedSnapshot)
        {
            // The snapshot is gone or belongs to another process when 'system-proxy reset'
            // already restored the settings or another instance took over. Restoring now
            // would undo that, so leave the settings alone.
            if (SystemProxySnapshotStore.Load(_snapshotPath)?.Pid != Environment.ProcessId)
            {
                return;
            }
        }

        restore();
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
            // Only touch the services recorded in the snapshot: services added while Dev
            // Proxy ran weren't changed by it, so turning their proxy off would lose their
            // settings. Without a snapshot, turn the proxy off for every service.
            restoredPrevious = snapshot?.MacOS is not null;
            succeeded = restoredPrevious
                ? RestoreMacOS(snapshot!.MacOS!)
                : RunToggleScript("off");
        }

        if (!succeeded)
        {
            // Keep the snapshot so a later reset can still put the previous settings back.
            return SystemProxyRestoreResult.Failed;
        }

        // Retire the registrations before deleting the snapshot. If that fails, a live
        // instance would still claim the system proxy and could undo the restored settings
        // when it stops or after it crashes, so report failure and keep the snapshot for a retry.
        if (!RetireSystemProxyRegistrations())
        {
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

    internal void SaveSnapshot(Action<SystemProxySnapshot> capture)
    {
        ProxyInstanceState? previousOwner = null;
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
            else if (snapshot.Pid != Environment.ProcessId)
            {
                previousOwner = new() { Pid = snapshot.Pid, ProcessStartTime = snapshot.ProcessStartTime };
            }
            snapshot.Pid = Environment.ProcessId;
            snapshot.ProcessStartTime = StateManager.GetCurrentProcessStartTime();
            SystemProxySnapshotStore.Save(_snapshotPath, snapshot);
            _savedSnapshot = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Couldn't save the current system proxy settings. They can't be restored when Dev Proxy stops.");
            return;
        }

        if (previousOwner is not null)
        {
            // The previous owner no longer owns the system proxy. Record that, so stopping
            // it or recovering after it crashes doesn't undo the settings this instance
            // restores when it stops.
            try
            {
                RunSynchronously(StateManager.ClearSystemProxyFlagAsync(previousOwner));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't record that Dev Proxy (PID: {Pid}) no longer owns the system proxy.", previousOwner.Pid);
            }
        }
    }

    /// <summary>
    /// Records that no instance owns the system proxy anymore after its settings were
    /// restored: live instances that registered as the system proxy are marked as no
    /// longer owning it, and records of dead ones are removed. Otherwise, stopping or
    /// recovering after any of them would change the restored settings again.
    /// </summary>
    private bool RetireSystemProxyRegistrations()
    {
        try
        {
            RunSynchronously(RetireSystemProxyRegistrationsAsync());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Couldn't update the records of Dev Proxy instances registered as the system proxy.");
            return false;
        }
    }

    private static async Task RetireSystemProxyRegistrationsAsync()
    {
        foreach (var state in (await StateManager.LoadAllStatesAsync()).Where(s => s.AsSystemProxy))
        {
            await StateManager.ClearSystemProxyFlagAsync(state);
        }

        foreach (var orphan in await StateManager.GetOrphanedSystemProxyStatesAsync())
        {
            await StateManager.DeleteStateAsync(orphan);
        }
    }

    // Called from the synchronous ISystemProxyManager members; the host has no
    // synchronization context, so blocking here can't deadlock.
#pragma warning disable VSTHRD002
    private static void RunSynchronously(Task task) => task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

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
        // Keep going after a failure so one broken service (for example, one that was
        // removed while Dev Proxy was running) doesn't leave the others without a proxy.
        var succeeded = true;
        foreach (var command in services.SelectMany(MacOSNetworkSetup.GetRestoreCommands))
        {
            try
            {
                _ = RunNetworkSetup(command);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _logger.LogError(ex, "Failed to restore the system proxy via networksetup {Arguments}.", string.Join(' ', command));
                succeeded = false;
            }
        }

        foreach (var (service, proxy, endpoint) in MacOSNetworkSetup.GetAuthenticatedEndpoints(services))
        {
            _logger.LogWarning(
                "Restored the {Proxy} proxy {Server}:{Port} for {Service}, but its password can't be restored. Turn on 'Proxy server requires password' for {Service} in System Settings > Network > Details > Proxies and enter your credentials.",
                proxy, endpoint.Server, endpoint.Port, service, service);
        }
        return succeeded;
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
    /// What happened to the OS proxy settings while reconciling orphaned registrations.
    /// </summary>
    internal enum OrphanRestoreOutcome
    {
        /// <summary>The OS proxy settings were restored.</summary>
        Restored,
        /// <summary>A live instance owns the system proxy, so the settings were left alone.</summary>
        OwnedByLiveInstance,
        /// <summary>Restoring the settings failed. The recovery records were kept for a retry.</summary>
        Failed
    }

    /// <summary>
    /// The outcome of reconciling orphaned system-proxy registrations.
    /// </summary>
    /// <param name="OrphanedPids">PIDs of dead instances that left a system-proxy registration behind.</param>
    /// <param name="Outcome">What happened to the OS proxy settings.</param>
    internal readonly record struct OrphanReconciliation(
        IReadOnlyList<int> OrphanedPids,
        OrphanRestoreOutcome Outcome);

    /// <summary>
    /// Finds the PID of a live process that owns the system proxy. The (foreground or
    /// detached) instance recorded in the snapshot file is authoritative, because the
    /// latest instance to take over the system proxy rewrites it. Without a snapshot,
    /// falls back to a running detached instance registered as the system proxy.
    /// Returns null when no live process owns it.
    /// </summary>
    internal static Task<int?> FindLiveSystemProxyOwnerAsync(CancellationToken cancellationToken = default) =>
        FindLiveSystemProxyOwnerAsync(SystemProxySnapshotStore.DefaultPath, cancellationToken);

    internal static async Task<int?> FindLiveSystemProxyOwnerAsync(string snapshotPath, CancellationToken cancellationToken = default)
    {
        var snapshot = SystemProxySnapshotStore.Load(snapshotPath);
        if (snapshot is not null)
        {
            return snapshot.IsOwnerAlive() ? snapshot.Pid : null;
        }

        var liveInstance = await StateManager.FindSystemProxyInstanceAsync(cancellationToken);
        return liveInstance?.Pid;
    }

    /// <summary>
    /// Reconciles system-proxy registrations left behind by crashed or killed instances.
    /// Restores the OS proxy (unless a live instance still owns it) and removes
    /// the stale state records. Safe to call when there are no orphans.
    /// </summary>
    public static async Task<OrphanReconciliation> ReconcileOrphanedSystemProxiesAsync(ILogger<SystemProxyManager>? logger = null, CancellationToken cancellationToken = default)
    {
        var orphans = await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken);
        var orphanedPids = orphans.Select(o => o.Pid).ToList();

        // A snapshot left by a dead instance without a state record (e.g. a crashed
        // foreground instance) is also an orphan.
        int? snapshotOnlyPid = null;
        var snapshot = SystemProxySnapshotStore.Load(SystemProxySnapshotStore.DefaultPath);
        if (snapshot is not null && !snapshot.IsOwnerAlive() && !orphanedPids.Contains(snapshot.Pid))
        {
            snapshotOnlyPid = snapshot.Pid;
            orphanedPids.Add(snapshot.Pid);
        }

        if (orphanedPids.Count == 0)
        {
            return new([], OrphanRestoreOutcome.Restored);
        }

        var outcome = OrphanRestoreOutcome.OwnedByLiveInstance;
        if (await FindLiveSystemProxyOwnerAsync(cancellationToken) is null)
        {
            if (new SystemProxyManager(logger ?? NullLogger<SystemProxyManager>.Instance).Restore() is SystemProxyRestoreResult.Failed)
            {
                // Keep the state records (and the snapshot) so the next start or stop retries.
                return new(orphanedPids, OrphanRestoreOutcome.Failed);
            }
            outcome = OrphanRestoreOutcome.Restored;
        }

        foreach (var orphan in orphans)
        {
            await StateManager.DeleteStateAsync(orphan, cancellationToken);
        }

        if (snapshotOnlyPid is int pid && snapshot is not null)
        {
            await StateManager.DeleteStateAsync(new ProxyInstanceState
            {
                Pid = pid,
                ProcessStartTime = snapshot.ProcessStartTime,
            }, cancellationToken);
        }

        return new(orphanedPids, outcome);
    }
}
