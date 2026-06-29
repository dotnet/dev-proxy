// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy;

/// <summary>What a root-trust implementation should do for the current platform/config.</summary>
public enum RootTrustAction
{
    /// <summary>Do nothing (trust disabled, already-trusted, or user declined).</summary>
    Skip,

    /// <summary>Trust the root in the macOS login keychain (first-run flow).</summary>
    TrustMacKeychain,

    /// <summary>Install the root into the Windows CurrentUser root store.</summary>
    TrustWindowsStore,

    /// <summary>Trust can't be automated on Linux; tell the user to trust manually.</summary>
    ManualLinux,
}

/// <summary>
/// Pure decision table for OS-trust installation, factored out of the platform I/O so it
/// can be exhaustively unit-tested. Mirrors today's behavior: Windows installs into the
/// user root store whenever cert install is enabled; macOS trusts via the keychain only on
/// first run (and only if the user doesn't decline); Linux is manual.
///
/// <code>
///                 installCert == false ─────────────────────► Skip
///                 │
///   ┌─ Windows ───┴──────────────────────────────────────────► TrustWindowsStore
///   │
///   ├─ macOS ── noFirstRun? ──yes──────────────────────────► Skip
///   │           │ no
///   │           ├─ already ran once (!isFirstRun)? ──yes───► Skip
///   │           ├─ answer == "n"? ──yes────────────────────► Skip
///   │           └─ otherwise ──────────────────────────────► TrustMacKeychain
///   │
///   └─ Linux ────────────────────────────────────────────────► ManualLinux
/// </code>
/// </summary>
public static class RootTrustPolicy
{
    public static RootTrustAction Decide(
        bool isMac,
        bool isWindows,
        bool installCert,
        bool noFirstRun,
        bool isFirstRun,
        string? firstRunAnswer)
    {
        if (!installCert)
        {
            return RootTrustAction.Skip;
        }

        if (isWindows)
        {
            return RootTrustAction.TrustWindowsStore;
        }

        if (isMac)
        {
            if (noFirstRun || !isFirstRun)
            {
                return RootTrustAction.Skip;
            }

            return string.Equals(firstRunAnswer?.Trim(), "n", StringComparison.OrdinalIgnoreCase)
                ? RootTrustAction.Skip
                : RootTrustAction.TrustMacKeychain;
        }

        return RootTrustAction.ManualLinux;
    }
}
