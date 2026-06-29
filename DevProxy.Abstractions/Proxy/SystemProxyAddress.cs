// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace DevProxy.Abstractions.Proxy;

/// <summary>
/// Pure helpers for composing the address clients should use to reach this proxy when it
/// is registered as the system proxy. Separated from the platform I/O so the
/// (only interesting) normalization logic is unit-testable.
///
/// <code>
///   bind address      system-proxy host
///   ─────────────     ─────────────────
///   (null/empty)  →   127.0.0.1     (no explicit bind ⇒ loopback)
///   0.0.0.0       →   127.0.0.1     (wildcard IPv4 ⇒ loopback for clients)
///   ::            →   127.0.0.1     (wildcard IPv6 ⇒ loopback for clients)
///   10.0.0.5      →   10.0.0.5      (explicit address passed through)
/// </code>
/// </summary>
public static class SystemProxyAddress
{
    /// <summary>
    /// Normalizes a bind address to the host clients should target. A wildcard or unset
    /// bind address can't be dialed by a client, so it collapses to loopback.
    /// </summary>
    public static string ResolveHost(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return "127.0.0.1";
        }

        return ipAddress is "0.0.0.0" or "::" ? "127.0.0.1" : ipAddress;
    }

    /// <summary>
    /// The <c>host:port</c> value used for the Windows <c>ProxyServer</c> registry setting.
    /// </summary>
    public static string ToHostPort(string? ipAddress, int port) =>
        $"{ResolveHost(ipAddress)}:{port.ToString(CultureInfo.InvariantCulture)}";
}
