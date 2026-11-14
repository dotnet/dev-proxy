// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.State;
using Microsoft.AspNetCore.Connections;
using System.Net;
using System.Net.Sockets;

namespace DevProxy.Proxy;

internal enum PortKind
{
    Proxy,
    Api
}

/// <summary>
/// Detects port conflicts for the proxy and API ports and produces
/// actionable error messages instead of raw socket exceptions.
/// </summary>
internal static class PortConflict
{
    /// <summary>
    /// Returns false only when binding the address fails because it's already in use.
    /// Any other failure (invalid address, permissions) is left for Kestrel to report.
    /// </summary>
    public static bool IsPortAvailable(string? ipAddress, int port)
    {
        if (port <= 0 || port > IPEndPoint.MaxPort)
        {
            return true;
        }

        if (!TryParseAddress(ipAddress, out var address))
        {
            return true;
        }

        using var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// Finds another Dev Proxy instance with a proxy or API listener on the given
    /// port whose bind address overlaps the requested one. Returns null when
    /// ownership can't be established.
    /// </summary>
    public static ProxyInstanceState? FindOwningInstance(
        IEnumerable<ProxyInstanceState> states, string? ipAddress, int port, int excludePid)
    {
        if (port <= 0 || !TryParseAddress(ipAddress, out var requested))
        {
            return null;
        }

        return states.FirstOrDefault(s => s.Pid != excludePid &&
            (ListensOn(s.ProxyUrl, s.Port, requested, port) || ListensOn(s.ApiUrl, null, requested, port)));
    }

    public static string FormatMessage(PortKind kind, int port, ProxyInstanceState? owner)
    {
        var option = kind == PortKind.Api ? DevProxyCommand.ApiPortOptionName : DevProxyCommand.PortOptionName;
        var configProperty = kind == PortKind.Api ? "apiPort" : "port";
        var portLabel = kind == PortKind.Api ? "API port" : "Port";
        var nl = Environment.NewLine;

        return owner is not null
            ? $"{portLabel} {port} is already in use by another Dev Proxy instance (PID: {owner.Pid}).{nl}" +
                $"Stop the existing instance with 'devproxy stop --pid {owner.Pid}' or use a different port:{nl}" +
                $"  devproxy {option} 0  (lets the OS assign a free port)"
            : $"{portLabel} {port} is already in use by another application.{nl}" +
                $"Use a different port:{nl}" +
                $"  devproxy {option} 0  (lets the OS assign a free port){nl}" +
                $"You can also set '{configProperty}' in your devproxyrc.json config file.";
    }

    /// <summary>
    /// Checks the given port and returns an error message if it's in use, or null if it's available.
    /// </summary>
    public static async Task<string?> CheckAsync(
        PortKind kind, string? ipAddress, int port, CancellationToken cancellationToken = default)
    {
        if (IsPortAvailable(ipAddress, port))
        {
            return null;
        }

        var message = await GetMessageAsync(kind, ipAddress, port, cancellationToken);
        return message;
    }

    public static async Task<string> GetMessageAsync(
        PortKind kind, string? ipAddress, int port, CancellationToken cancellationToken = default)
    {
        ProxyInstanceState? owner = null;
        try
        {
            // Don't prune state files here: they may be needed to recover an
            // orphaned system-proxy registration.
            var states = await StateManager.LoadRunningStatesAsync(cancellationToken);
            owner = FindOwningInstance(states, ipAddress, port, Environment.ProcessId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // State files are best-effort; fall back to the generic message.
        }

        return FormatMessage(kind, port, owner);
    }

    public static bool IsAddressInUse(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException ||
                current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }
        return false;
    }

    private static bool ListensOn(string url, int? port, IPAddress requestedAddress, int requestedPort)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (port ?? uri.Port) != requestedPort ||
            !TryParseAddress(uri.Host, out var address))
        {
            return false;
        }

        return address.Equals(requestedAddress) || Covers(address, requestedAddress) || Covers(requestedAddress, address);
    }

    // An IPv6 wildcard listener is dual-stack, but an IPv4 wildcard only covers IPv4 addresses.
    private static bool Covers(IPAddress wildcard, IPAddress address) =>
        wildcard.Equals(IPAddress.IPv6Any) ||
        (wildcard.Equals(IPAddress.Any) && address.AddressFamily == AddressFamily.InterNetwork);

    private static bool TryParseAddress(string? ipAddress, out IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            address = IPAddress.Loopback;
            return true;
        }

        return IPAddress.TryParse(ipAddress.Trim('[', ']'), out address!);
    }
}
