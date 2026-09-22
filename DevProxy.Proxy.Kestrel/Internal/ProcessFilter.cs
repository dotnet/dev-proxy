// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

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
/// The PID, process metadata, and process-tree resolvers are injectable so the decision
/// logic can be unit-tested without querying real processes; the defaults shell out to
/// <see cref="ConnectionProcessResolver"/> / <c>ps</c>, use the Windows process snapshot
/// API, and read metadata through <see cref="Process.GetProcessById"/>.
/// </para>
/// </summary>
internal sealed class ProcessFilter
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private const int MaxCacheEntries = 4096;

    private readonly HashSet<int> _pids;
    // Ordinal (case-sensitive) to match the Titanium engine's IEnumerable.Contains.
    private readonly HashSet<string> _names;
    private readonly bool _watchProcessTree;
    private readonly Func<int, int?> _resolvePid;
    private readonly Func<int, string?> _resolveName;
    private readonly Func<int, DateTimeOffset?> _resolveStartTime;
    private readonly Func<IReadOnlyDictionary<int, int>> _resolveParentPids;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<ProcessCacheKey, CachedDecision> _cache = new();

    public ProcessFilter(
        IEnumerable<int> watchPids,
        IEnumerable<string> watchProcessNames,
        bool watchProcessTree = false,
        Func<int, int?>? resolvePid = null,
        Func<int, string?>? resolveName = null,
        Func<int, DateTimeOffset?>? resolveStartTime = null,
        Func<IReadOnlyDictionary<int, int>>? resolveParentPids = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(watchPids);
        ArgumentNullException.ThrowIfNull(watchProcessNames);
        _pids = [.. watchPids];
        _names = new HashSet<string>(watchProcessNames, StringComparer.Ordinal);
        _watchProcessTree = watchProcessTree;
        _resolvePid = resolvePid ?? ConnectionProcessResolver.ResolveProcessId;
        _resolveName = resolveName ?? DefaultResolveName;
        _resolveStartTime = resolveStartTime ?? DefaultResolveStartTime;
        _resolveParentPids = resolveParentPids ?? ProcessTreeResolver.ResolveParentProcessIds;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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

        if (MatchesProcessName(pid.Value))
        {
            return true;
        }

        if (!_watchProcessTree)
        {
            return false;
        }

        var cacheKeys = new List<ProcessCacheKey>();
        var processKey = ResolveCacheKey(pid.Value);
        if (processKey is null)
        {
            // Process start time is required to validate that each parent PID still
            // identifies the process that originally created its child.
            return false;
        }

        if (TryGetCachedDecision(processKey.Value, out var cached))
        {
            return cached;
        }
        cacheKeys.Add(processKey.Value);

        var parentPids = _resolveParentPids();
        if (parentPids.Count == 0)
        {
            return false;
        }

        var visited = new HashSet<int> { pid.Value };
        var currentPid = pid.Value;
        var currentKey = processKey.Value;
        var isWatched = false;
        while (parentPids.TryGetValue(currentPid, out var parentPid) &&
               parentPid > 0 &&
               visited.Add(parentPid))
        {
            var parentKey = ResolveCacheKey(parentPid);
            if (parentKey is null ||
                parentKey.Value.StartTimeUtcTicks > currentKey.StartTimeUtcTicks)
            {
                // PROCESSENTRY32 and similar process tables retain only the creator PID.
                // If that PID was reused after the child started, it is no longer an
                // ancestor. Missing start times cannot establish a safe relationship.
                break;
            }

            if (TryGetCachedDecision(parentKey.Value, out cached))
            {
                isWatched = cached;
                break;
            }
            cacheKeys.Add(parentKey.Value);

            if (_pids.Contains(parentPid) || MatchesProcessName(parentPid))
            {
                isWatched = true;
                break;
            }

            currentPid = parentPid;
            currentKey = parentKey.Value;
        }

        foreach (var key in cacheKeys)
        {
            CacheDecision(key, isWatched);
        }

        return isWatched;
    }

    private bool MatchesProcessName(int pid)
    {
        if (_names.Count == 0)
        {
            return false;
        }

        var name = _resolveName(pid);
        return name is not null && _names.Contains(name);
    }

    private ProcessCacheKey? ResolveCacheKey(int pid)
    {
        var startTime = _resolveStartTime(pid);
        return startTime is null ? null : new(pid, startTime.Value.UtcTicks);
    }

    private bool TryGetCachedDecision(ProcessCacheKey key, out bool isWatched)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _utcNow())
            {
                isWatched = entry.IsWatched;
                return true;
            }

            _ = _cache.TryRemove(key, out _);
        }

        isWatched = false;
        return false;
    }

    private void CacheDecision(ProcessCacheKey key, bool isWatched)
    {
        var now = _utcNow();
        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var entry in _cache)
            {
                if (entry.Value.ExpiresAt <= now)
                {
                    _ = _cache.TryRemove(entry.Key, out _);
                }
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[key] = new(isWatched, now.Add(CacheLifetime));
    }

    private static string? DefaultResolveName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process exited or its metadata is unavailable.
            return null;
        }
    }

    private static DateTimeOffset? DefaultResolveStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private readonly record struct ProcessCacheKey(int Pid, long StartTimeUtcTicks);
    private readonly record struct CachedDecision(bool IsWatched, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Captures the current process parent relationships. A single snapshot is used for each
/// uncached process-tree decision so walking a deep hierarchy does not repeatedly query the OS.
/// </summary>
internal static class ProcessTreeResolver
{
    public static IReadOnlyDictionary<int, int> ResolveParentProcessIds()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? ResolveWindowsParentProcessIds()
                : ResolveUnixParentProcessIds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new Dictionary<int, int>();
        }
    }

    internal static Dictionary<int, int> ParsePsOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var parentPids = new Dictionary<int, int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
            {
                parentPids[pid] = parentPid;
            }
        }

        return parentPids;
    }

    private static Dictionary<int, int> ResolveUnixParentProcessIds()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ps",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new Dictionary<int, int>();
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? ParsePsOutput(output)
            : new Dictionary<int, int>();
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, int> ResolveWindowsParentProcessIds()
    {
        var parentPids = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot.IsInvalid)
        {
            return parentPids;
        }

        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>()
        };
        if (!Process32First(snapshot, ref entry))
        {
            return parentPids;
        }

        do
        {
            parentPids[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        return parentPids;
    }

    private const uint Th32csSnapprocess = 0x00000002;
    private const int MaxPath = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string ExecutableFile;
    }

#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);
#pragma warning restore SYSLIB1054
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
