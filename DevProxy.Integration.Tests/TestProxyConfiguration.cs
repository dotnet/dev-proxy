// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Minimal <see cref="IProxyConfiguration"/> for the integration harness. Only the
/// members the Kestrel engine reads at boot (Port, IPAddress, AsSystemProxy,
/// WatchPids/WatchProcessNames) actually matter; the rest carry inert defaults.
/// </summary>
internal sealed class TestProxyConfiguration : IProxyConfiguration
{
    public int ApiPort { get; set; }
    public bool AsSystemProxy { get; set; }
    public string ConfigFile { get; set; } = "devproxyrc.json";
    public Dictionary<string, string> Env { get; set; } = [];
    public IEnumerable<MockRequestHeader>? FilterByHeaders { get; }
    public bool InstallCert { get; set; }
    public string? IPAddress { get; set; } = "127.0.0.1";
    public OutputFormat Output { get; set; } = OutputFormat.Text;
    public LogLevel LogLevel => LogLevel.Information;
    public ReleaseType NewVersionNotification => ReleaseType.None;
    public bool NoFirstRun { get; set; } = true;
    public bool NoWatch { get; set; }
    public int Port { get; set; }
    public bool Record { get; set; }
    public bool ShowTimestamps => false;
    public long? TimeoutSeconds { get; set; }
    public bool ValidateSchemas => false;
    public IEnumerable<int> WatchPids { get; set; } = [];
    public IEnumerable<string> WatchProcessNames { get; set; } = [];
}
