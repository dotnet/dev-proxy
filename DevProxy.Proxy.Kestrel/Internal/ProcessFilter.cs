// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Decides whether a connection's owning process is one the user asked to watch
/// (<c>--watch-pids</c> / <c>--watch-process-names</c>). Mirrors the Titanium engine's
/// <c>IsProxiedProcess</c>: when no process filter is configured every process is
/// watched; otherwise the client connection's source port is resolved to a PID and
/// matched against the configured pids/names. A process that cannot be resolved is NOT
/// watched (the connection is blind-tunnelled rather than decrypted).
///
/// <para>
/// Like the Titanium engine, this is applied only at the <c>CONNECT</c> (HTTPS) decision
/// point — plain-HTTP requests are never process-filtered.
/// </para>
///
/// <para>
/// The PID resolver and name resolver are injectable so the decision logic can be
/// unit-tested without spawning real processes; the defaults shell out to
/// <see cref="ConnectionProcessResolver"/> and <see cref="Process.GetProcessById"/>.
/// </para>
/// </summary>
internal sealed class ProcessFilter
{
    private readonly HashSet<int> _pids;
    // Ordinal (case-sensitive) to match the Titanium engine's IEnumerable.Contains.
    private readonly HashSet<string> _names;
    private readonly Func<int, int?> _resolvePid;
    private readonly Func<int, string?> _resolveName;

    public ProcessFilter(
        IEnumerable<int> watchPids,
        IEnumerable<string> watchProcessNames,
        Func<int, int?>? resolvePid = null,
        Func<int, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(watchPids);
        ArgumentNullException.ThrowIfNull(watchProcessNames);
        _pids = [.. watchPids];
        _names = new HashSet<string>(watchProcessNames, StringComparer.Ordinal);
        _resolvePid = resolvePid ?? ConnectionProcessResolver.ResolveProcessId;
        _resolveName = resolveName ?? DefaultResolveName;
    }

    /// <summary>True when no pid/name filter is configured — every process is watched.</summary>
    public bool IsEmpty => _pids.Count == 0 && _names.Count == 0;

    /// <summary>
    /// Whether the process owning the connection with the given client source port is
    /// watched. Returns true immediately when no filter is configured.
    /// </summary>
    public bool IsWatchedProcess(int clientPort)
    {
        if (IsEmpty)
        {
            return true;
        }

        var pid = _resolvePid(clientPort);
        if (pid is null or -1)
        {
            // Couldn't identify the owning process — don't decrypt it.
            return false;
        }

        if (_pids.Contains(pid.Value))
        {
            return true;
        }

        if (_names.Count > 0)
        {
            var name = _resolveName(pid.Value);
            if (name is not null && _names.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private static string? DefaultResolveName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (ArgumentException)
        {
            // Process has already exited.
            return null;
        }
    }
}

/// <summary>
/// Resolves the PID owning a TCP connection by its client (source) port, by shelling out
/// to the platform's connection-listing tool and parsing the output:
/// <c>lsof -i :PORT</c> on Unix, <c>netstat -ano -p tcp</c> on Windows. Returns
/// <see langword="null"/> when the tool fails or no matching connection is found.
/// </summary>
internal static class ConnectionProcessResolver
{
    public static int? ResolveProcessId(int clientPort)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? RunAndParse("netstat", "-ano -p tcp", o => NetstatParser.ParsePid(o, clientPort))
                : RunAndParse("lsof", $"-i :{clientPort.ToString(CultureInfo.InvariantCulture)}",
                    o => LsofParser.ParsePid(o, clientPort));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The listing tool is missing or could not be launched.
            return null;
        }
    }

    private static int? RunAndParse(string fileName, string arguments, Func<string, int?> parse)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return null;
        }

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return parse(output);
    }
}

/// <summary>
/// Pure parser for <c>lsof -i :PORT</c> output. The client's connection appears as a
/// <c>…:CLIENTPORT-&gt;…</c> entry (the proxy's own socket is the reverse,
/// <c>…-&gt;…:CLIENTPORT</c>, so anchoring on <c>CLIENTPORT-&gt;</c> selects the client's
/// process). The PID is the second whitespace-delimited column (<c>COMMAND PID …</c>).
/// </summary>
internal static partial class LsofParser
{
    public static int? ParsePid(string lsofOutput, int clientPort)
    {
        ArgumentNullException.ThrowIfNull(lsofOutput);

        var marker = $"{clientPort.ToString(CultureInfo.InvariantCulture)}->";
        foreach (var line in lsofOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var match = PidColumn().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    // COMMAND token, then whitespace, then the PID digits.
    [GeneratedRegex(@"^\S+\s+(\d+)")]
    private static partial Regex PidColumn();
}

/// <summary>
/// Pure parser for Windows <c>netstat -ano -p tcp</c> output. Each connection row is
/// <c>Proto  LocalAddress  ForeignAddress  State  PID</c>; the client's socket is the row
/// whose LOCAL address ends with the client source port, and its PID is the last column.
/// </summary>
internal static class NetstatParser
{
    public static int? ParsePid(string netstatOutput, int clientPort)
    {
        ArgumentNullException.ThrowIfNull(netstatOutput);

        var suffix = $":{clientPort.ToString(CultureInfo.InvariantCulture)}";
        foreach (var line in netstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts[1].EndsWith(suffix, StringComparison.Ordinal)
                && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return null;
    }
}
