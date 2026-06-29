// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy;

/// <summary>
/// Turns the operating-system HTTP/HTTPS proxy on and off. Engine-agnostic: the Kestrel
/// engine and the <c>stop</c> command's crash-cleanup path drive the same implementation,
/// so there is one place that owns the OS proxy state.
///
/// <para>
/// The implementation lives in the host (it needs platform I/O — the Windows registry +
/// WinINET refresh, the macOS <c>toggle-proxy.sh</c> script). The Kestrel engine project
/// cannot reference the host, so it receives this through the abstraction, exactly like
/// <see cref="IRootCertificateTrust"/>.
/// </para>
/// </summary>
public interface ISystemProxyManager
{
    /// <summary>
    /// Registers Dev Proxy as the system HTTP/HTTPS proxy at <paramref name="ipAddress"/>:
    /// <paramref name="port"/>. Wildcard bind addresses are normalized to loopback.
    /// </summary>
    void Enable(string? ipAddress, int port);

    /// <summary>
    /// Removes Dev Proxy as the system proxy, restoring direct connections. Safe to call
    /// even if the proxy was never enabled (idempotent best-effort cleanup).
    /// </summary>
    void Disable();
}
