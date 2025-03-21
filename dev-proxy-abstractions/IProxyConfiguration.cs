// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;

namespace DevProxy.Abstractions;

public interface IProxyConfiguration
{
    int ApiPort { get; }
    bool AsSystemProxy { get; }
    string ConfigFile { get; }
    Dictionary<string, string> Env { get; set; }
    MockRequestHeader[]? FilterByHeaders { get; }
    bool InstallCert { get; }
    string? IPAddress { get; }
    LogLevel LogLevel { get; }
    bool NoFirstRun { get; }
    int Port { get; }
    bool Record { get; }
    bool ShowTimestamps { get; }
    long? TimeoutSeconds { get; }
    bool ValidateSchemas { get; }
    IEnumerable<int> WatchPids { get; }
    IEnumerable<string> WatchProcessNames { get; }
}