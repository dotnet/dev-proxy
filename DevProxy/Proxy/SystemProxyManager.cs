// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.State;
using System.Diagnostics;
using Titanium.Web.Proxy;

namespace DevProxy.Proxy;

/// <summary>
/// Restores the operating system proxy settings out-of-process.
/// Used to recover from a Dev Proxy instance that registered itself as the
/// system proxy but was terminated without running its normal cleanup (crash,
/// SIGKILL, OOM, power loss). The operation is best-effort, idempotent, and
/// safe to call even when no system proxy is currently configured.
/// </summary>
internal static class SystemProxyManager
{
    /// <summary>
    /// The outcome of reconciling orphaned system-proxy registrations.
    /// </summary>
    /// <param name="Orphans">The orphaned instance records that were reconciled.</param>
    /// <param name="SystemProxyDisabled">
    /// True if the OS system proxy was disabled as part of reconciliation. This
    /// is false when a live instance still owns the system proxy, in which case
    /// only the stale state records are removed.
    /// </param>
    internal readonly record struct OrphanReconciliation(
        IReadOnlyList<ProxyInstanceState> Orphans,
        bool SystemProxyDisabled);

    /// <summary>
    /// Disables the operating system proxy.
    /// On Windows this clears the WinINET system proxy settings; on macOS it
    /// runs <c>toggle-proxy.sh off</c>. On Linux this is a no-op because Dev
    /// Proxy never configures a system proxy there.
    /// </summary>
    public static void Disable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DisableWindows();
            }
            else if (OperatingSystem.IsMacOS())
            {
                DisableMacOS();
            }
            // Linux: Dev Proxy never sets a system proxy, so there's nothing to restore.
        }
        catch
        {
            // Best-effort cleanup — never block the stop flow.
        }
    }

    /// <summary>
    /// Reconciles system-proxy registrations left behind by crashed instances.
    /// Restores the OS proxy (unless a live instance still owns it) and removes
    /// the stale state records. Safe to call when there are no orphans.
    /// </summary>
    public static async Task<OrphanReconciliation> ReconcileOrphanedSystemProxiesAsync(CancellationToken cancellationToken = default)
    {
        // Capture orphans before any liveness-pruning call deletes their state files.
        var orphans = await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken);
        if (orphans.Count == 0)
        {
            return new([], false);
        }

        // Only touch the global OS proxy setting if no live instance currently
        // owns it — otherwise we'd disable a proxy a running instance depends on.
        var liveOwner = await StateManager.FindSystemProxyInstanceAsync(cancellationToken);
        var disabled = false;
        if (liveOwner is null)
        {
            Disable();
            disabled = true;
        }

        foreach (var orphan in orphans)
        {
            await StateManager.DeleteStateAsync(orphan.Pid, cancellationToken);
        }

        return new(orphans, disabled);
    }

    private static void DisableWindows()
    {
        // DisableAllSystemProxies clears the WinINET proxy settings directly and
        // does not require a running proxy, so a fresh instance is enough to undo
        // a registration left behind by a crashed process.
        using var proxyServer = new ProxyServer(userTrustRootCertificate: false);
        proxyServer.DisableAllSystemProxies();
    }

    private static void DisableMacOS()
    {
        var bashScriptPath = Path.Join(ProxyUtils.AppFolder ?? AppContext.BaseDirectory, "toggle-proxy.sh");
        if (!File.Exists(bashScriptPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(bashScriptPath);
        startInfo.ArgumentList.Add("off");

        using var process = new Process { StartInfo = startInfo };
        _ = process.Start();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill();
        }
    }
}
