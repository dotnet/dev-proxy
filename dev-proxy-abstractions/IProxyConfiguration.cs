// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Abstractions;

public interface IProxyConfiguration
{
    int ApiPort { get; }
    bool AsSystemProxy { get; }
    string? IPAddress { get; }
    string ConfigFile { get; }
    bool InstallCert { get; }
    MockRequestHeader[]? FilterByHeaders { get; }
    LogLevel LogLevel { get; }
    bool NoFirstRun { get; }
    int Port { get; }
    int Rate { get; }
    bool Record { get; }
    IEnumerable<int> WatchPids { get; }
    IEnumerable<string> WatchProcessNames { get; }
    bool ShowTimestamps { get; }
}