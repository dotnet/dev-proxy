// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevProxy.Proxy;

/// <summary>
/// Keeps a spawned detached daemon from holding on to the caller's stdio.
/// </summary>
/// <remarks>
/// On Windows, <see cref="System.Diagnostics.Process.Start()"/> calls <c>CreateProcess</c>
/// with <c>bInheritHandles = TRUE</c>, so the child inherits every inheritable handle of the
/// parent, not just the ones assigned as its std handles. The std handles a caller passes in
/// (for example the pipe behind <c>devproxy --detach | Out-String</c>) are inheritable, so the
/// long-running daemon would keep the caller's pipe open and the caller would never see EOF.
/// On Unix, <c>fork</c>/<c>exec</c> replaces the child's fds 0-2 with the redirected pipes, so
/// no extra work is needed there.
/// </remarks>
internal static class StandardHandleInheritance
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Marks this process's stdin, stdout, and stderr handles as non-inheritable so that child
    /// processes started afterwards don't inherit them. This doesn't affect this process's own
    /// use of the handles. No-op on non-Windows platforms.
    /// </summary>
    public static void DisableForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var stdHandle in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            DisableInheritance(stdHandle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DisableInheritance(int stdHandle)
    {
        var handle = GetStdHandle(stdHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        // Best effort: if this fails, the daemon still starts; the caller may just not see EOF.
        _ = SetHandleInformation(handle, HandleFlagInherit, 0);
    }

    // DllImport (not LibraryImport) is used deliberately: LibraryImport's source generator
    // requires AllowUnsafeBlocks project-wide, which we avoid for these system calls.
#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
#pragma warning restore SYSLIB1054
}
