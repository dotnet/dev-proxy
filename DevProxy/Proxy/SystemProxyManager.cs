// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevProxy.Abstractions.Proxy;
using Microsoft.Win32;

namespace DevProxy.Proxy;

/// <summary>
/// Host-side <see cref="ISystemProxyManager"/>. Engine-agnostic OS proxy on/off, shared by
/// the Kestrel engine and the <c>stop --force</c> crash-cleanup path.
///
/// <code>
///   Enable(ip, port)                 Disable()
///   ────────────────                 ─────────
///   Windows → registry ProxyServer   Windows → registry ProxyEnable = 0
///             + ProxyEnable = 1                + WinINET refresh
///             + WinINET refresh
///   macOS   → toggle-proxy.sh on …   macOS   → toggle-proxy.sh off
///   Linux   → log warning            Linux   → no-op
/// </code>
///
/// <para>
/// The Windows path uses WinINET: it writes the per-user Internet Settings registry values
/// and then broadcasts INTERNET_OPTION_SETTINGS_CHANGED + INTERNET_OPTION_REFRESH so running
/// applications re-read the proxy without a restart. This is the standard WinINET mechanism;
/// it cannot be exercised on non-Windows hosts.
/// </para>
/// </summary>
internal sealed class SystemProxyManager(ILogger<SystemProxyManager> logger) : ISystemProxyManager
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public void Enable(string? ipAddress, int port)
    {
        if (OperatingSystem.IsWindows())
        {
            EnableWindows(SystemProxyAddress.ToHostPort(ipAddress, port));
        }
        else if (OperatingSystem.IsMacOS())
        {
            RunToggleScript($"on {SystemProxyAddress.ResolveHost(ipAddress)} {port}");
        }
        else
        {
            logger.LogWarning(
                "Configure your operating system to use this proxy's port and address {Address}:{Port}",
                SystemProxyAddress.ResolveHost(ipAddress),
                port);
        }
    }

    public void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            DisableWindows();
        }
        else if (OperatingSystem.IsMacOS())
        {
            RunToggleScript("off");
        }
    }

    [SupportedOSPlatform("windows")]
    private void EnableWindows(string hostPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                logger.LogError("Could not open the Windows Internet Settings registry key.");
                return;
            }

            key.SetValue("ProxyServer", hostPort, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            NotifyWinInetSettingsChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogError(ex, "Failed to set the system proxy via the Windows registry.");
        }
    }

    [SupportedOSPlatform("windows")]
    private void DisableWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifyWinInetSettingsChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogError(ex, "Failed to clear the system proxy via the Windows registry.");
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

    private void RunToggleScript(string arguments)
    {
        var bashScriptPath = Path.Join(AppContext.BaseDirectory, "toggle-proxy.sh");
        if (!File.Exists(bashScriptPath))
        {
            logger.LogWarning("Could not find {Script} to toggle the system proxy.", "toggle-proxy.sh");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"{bashScriptPath} {arguments}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            _ = process.Start();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Failed to toggle the system proxy via toggle-proxy.sh.");
        }
    }
}
