// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// A parsed <c>CONNECT</c> authority. <see cref="Host"/> is the bare host used for socket
/// connect / certificate minting (IPv6 literals are UNbracketed, e.g. <c>::1</c>);
/// <see cref="UrlHost"/> re-adds the brackets so it can be dropped into an absolute URL
/// (<c>https://[::1]:8443/path</c>), which would otherwise be ambiguous.
/// </summary>
internal readonly record struct ConnectAuthority(string Host, int Port, bool IsIPv6)
{
    /// <summary>The host in URL form: bracketed for IPv6 literals, bare otherwise.</summary>
    public string UrlHost => IsIPv6 ? $"[{Host}]" : Host;
}

/// <summary>
/// Parses the authority component of a <c>CONNECT host:port</c> request target. The naive
/// "split on the last colon" approach breaks on IPv6 literals (which are full of colons)
/// and silently accepts malformed input, so this is a small explicit state machine instead.
///
/// <code>
///   authority
///     │
///     ├─ starts with '[' ──► IPv6 literal: [addr] or [addr]:port
///     │                        • require a closing ']'
///     │                        • after ']' allow nothing or ":port"
///     │                        • addr must parse as a real IPv6 address
///     │
///     └─ otherwise ────────► reg-name / IPv4: host or host:port
///                              • at most ONE colon (a bare IPv6 like ::1 is rejected —
///                                clients must bracket it)
///                              • host must be a valid host name / IPv4 literal
///
///   port (when present): integer in 1..65535, else reject (→ 400)
/// </code>
/// </summary>
internal static class ConnectAuthorityParser
{
    public static bool TryParse(string authority, int defaultPort, out ConnectAuthority result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        string host;
        string? portText;
        bool isIPv6;

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false; // unterminated '['
            }

            host = authority[1..close];
            isIPv6 = true;

            var rest = authority[(close + 1)..];
            if (rest.Length == 0)
            {
                portText = null;
            }
            else if (rest[0] == ':')
            {
                portText = rest[1..];
            }
            else
            {
                return false; // junk after ']'
            }

            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false; // not a real IPv6 literal
            }
        }
        else
        {
            var firstColon = authority.IndexOf(':', StringComparison.Ordinal);
            var lastColon = authority.LastIndexOf(':');
            if (firstColon != lastColon)
            {
                return false; // multiple colons without brackets (bare IPv6 / garbage)
            }

            isIPv6 = false;
            if (firstColon < 0)
            {
                host = authority;
                portText = null;
            }
            else
            {
                host = authority[..firstColon];
                portText = authority[(firstColon + 1)..];
            }

            if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                return false; // empty or invalid host name / IPv4 literal
            }
        }

        var port = defaultPort;
        if (portText is not null)
        {
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535)
            {
                return false; // missing, non-numeric, or out-of-range port
            }
        }

        result = new ConnectAuthority(host, port, isIPv6);
        return true;
    }
}
