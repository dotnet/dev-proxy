// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;

namespace DevProxy;

public enum ReleaseType
{
    [EnumMember(Value = "none")]
    None,
    [EnumMember(Value = "stable")]
    Stable,
    [EnumMember(Value = "beta")]
    Beta
}

public class ProxyConfiguration : IProxyConfiguration
{
    public int ApiPort { get; set; } = 8897;
    public bool AsSystemProxy { get; set; } = true;
    public string ConfigFile { get; set; } = "devproxyrc.json";
    public MockRequestHeader[]? FilterByHeaders { get; set; }
    public string? IPAddress { get; set; } = "127.0.0.1";
    public bool InstallCert { get; set; } = true;
    public LanguageModelConfiguration? LanguageModel { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool NoFirstRun { get; set; } = false;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReleaseType NewVersionNotification { get; set; } = ReleaseType.Stable;
    internal List<PluginReference> Plugins { get; set; } = [];
    public int Port { get; set; } = 8000;
    public bool Record { get; set; } = false;
    public bool ShowSkipMessages { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public long? TimeoutSeconds { get; set; }
    internal List<string> UrlsToWatch { get; set; } = [];
    public bool ValidateSchemas { get; set; } = true;
    public IEnumerable<int> WatchPids { get; set; } = new List<int>();
    public IEnumerable<string> WatchProcessNames { get; set; } = [];
}
