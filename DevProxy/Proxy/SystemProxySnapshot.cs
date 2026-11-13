// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProxy.State;

namespace DevProxy.Proxy;

/// <summary>
/// The OS proxy settings that were in place before Dev Proxy registered itself as the
/// system proxy, plus the PID of the instance that did it. Persisted to disk so the
/// settings can be restored even if that instance is killed or crashes.
/// </summary>
internal sealed class SystemProxySnapshot
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("windows")]
    public WindowsProxySettings? Windows { get; set; }

    [JsonPropertyName("macOS")]
    public List<MacOSServiceProxySettings>? MacOS { get; set; }
}

internal sealed class WindowsProxySettings
{
    /// <summary>The previous <c>ProxyEnable</c> registry value; null when the value didn't exist.</summary>
    [JsonPropertyName("proxyEnable")]
    public int? ProxyEnable { get; set; }

    /// <summary>The previous <c>ProxyServer</c> registry value; null when the value didn't exist.</summary>
    [JsonPropertyName("proxyServer")]
    public string? ProxyServer { get; set; }
}

internal sealed class MacOSServiceProxySettings
{
    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("web")]
    public MacOSProxyEndpoint? Web { get; set; }

    [JsonPropertyName("secureWeb")]
    public MacOSProxyEndpoint? SecureWeb { get; set; }
}

internal sealed class MacOSProxyEndpoint
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>
/// Reads and writes the <see cref="SystemProxySnapshot"/> file.
/// </summary>
internal static class SystemProxySnapshotStore
{
    private const string FileName = "system-proxy-backup.json";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(StateManager.GetConfigFolder(), FileName);

    public static SystemProxySnapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SystemProxySnapshot>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, SystemProxySnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        // Called from the synchronous ISystemProxyManager.Enable; the host has no
        // synchronization context, so blocking here can't deadlock.
#pragma warning disable VSTHRD002
        PrivateFiles.WriteAllTextAsync(path, json).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Pure helpers for reading and restoring macOS proxy settings with <c>networksetup</c>.
/// </summary>
internal static class MacOSNetworkSetup
{
    public const string Executable = "/usr/sbin/networksetup";

    /// <summary>
    /// Parses <c>networksetup -listallnetworkservices</c>. The first line is an informational
    /// header and services prefixed with <c>*</c> are disabled, so both are skipped.
    /// </summary>
    public static List<string> ParseNetworkServices(string output)
    {
        return [.. output
            .Split('\n')
            .Skip(1)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('*'))];
    }

    /// <summary>
    /// Parses <c>networksetup -getwebproxy</c> / <c>-getsecurewebproxy</c> output.
    /// </summary>
    public static MacOSProxyEndpoint ParseProxyEndpoint(string output)
    {
        var endpoint = new MacOSProxyEndpoint();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "Enabled":
                    endpoint.Enabled = value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "Server":
                    endpoint.Server = value;
                    break;
                case "Port":
                    endpoint.Port = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0;
                    break;
                default:
                    break;
            }
        }
        return endpoint;
    }

    /// <summary>
    /// Builds the <c>networksetup</c> argument lists that put back a service's previous
    /// proxy server and state. Assumes the proxy has already been turned off for every
    /// service, so endpoints without a previous server need no commands.
    /// </summary>
    public static List<string[]> GetRestoreCommands(MacOSServiceProxySettings settings)
    {
        var commands = new List<string[]>();
        AddEndpointCommands(commands, settings.Service, settings.Web, "-setwebproxy", "-setwebproxystate");
        AddEndpointCommands(commands, settings.Service, settings.SecureWeb, "-setsecurewebproxy", "-setsecurewebproxystate");
        return commands;
    }

    private static void AddEndpointCommands(
        List<string[]> commands,
        string service,
        MacOSProxyEndpoint? endpoint,
        string setProxyCommand,
        string setStateCommand)
    {
        if (endpoint is null || string.IsNullOrEmpty(endpoint.Server) || endpoint.Port <= 0)
        {
            return;
        }

        // Setting the server also turns the proxy on, so turn it back off if it was disabled.
        commands.Add([setProxyCommand, service, endpoint.Server, endpoint.Port.ToString(CultureInfo.InvariantCulture)]);
        if (!endpoint.Enabled)
        {
            commands.Add([setStateCommand, service, "off"]);
        }
    }
}
