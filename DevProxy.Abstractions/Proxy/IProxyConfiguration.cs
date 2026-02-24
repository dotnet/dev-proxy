// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.Serialization;

namespace DevProxy.Abstractions.Proxy;

public enum ReleaseType
{
    [EnumMember(Value = "none")]
    None,
    [EnumMember(Value = "stable")]
    Stable,
    [EnumMember(Value = "beta")]
    Beta
}

public enum LogFor
{
    [EnumMember(Value = "human")]
    Human,
    [EnumMember(Value = "machine")]
    Machine
}

public interface IProxyConfiguration
{
    int ApiPort { get; set; }
    bool AsSystemProxy { get; set; }
    string ConfigFile { get; }
#pragma warning disable CA2227
    Dictionary<string, string> Env { get; set; }
#pragma warning restore CA2227
    IEnumerable<MockRequestHeader>? FilterByHeaders { get; }
    bool InstallCert { get; set; }
    string? IPAddress { get; set; }
    LogFor LogFor { get; set; }
    LogLevel LogLevel { get; }
    ReleaseType NewVersionNotification { get; }
    bool NoFirstRun { get; set; }
    int Port { get; set; }
    bool Record { get; set; }
    bool ShowTimestamps { get; }
    long? TimeoutSeconds { get; set; }
    bool ValidateSchemas { get; }
    IEnumerable<int> WatchPids { get; set; }
    IEnumerable<string> WatchProcessNames { get; set; }
}