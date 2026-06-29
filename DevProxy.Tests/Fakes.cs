// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Microsoft.Extensions.Logging;

namespace DevProxy.Tests;

/// <summary>Records hotkey-driven controller calls so tests can assert dispatch.</summary>
internal sealed class FakeProxyStateController : IProxyStateController
{
    public int StartRecordingCalls { get; private set; }
    public int StopRecordingCalls { get; private set; }
    public int MockRequestCalls { get; private set; }
    public int StopProxyCalls { get; private set; }

    public IProxyState ProxyState { get; } = new FakeProxyState();

    public void StartRecording() => StartRecordingCalls++;

    public Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        StopRecordingCalls++;
        return Task.CompletedTask;
    }

    public Task MockRequestAsync(CancellationToken cancellationToken)
    {
        MockRequestCalls++;
        return Task.CompletedTask;
    }

    public void StopProxy() => StopProxyCalls++;
}

internal sealed class FakeProxyState : IProxyState
{
    public Dictionary<string, object> GlobalData { get; } = [];
    public bool IsRecording { get; set; }
    public ConcurrentQueue<RequestLog> RequestLogs { get; } = new();
}

/// <summary>In-memory <see cref="ISystemConsole"/> capturing output for assertions.</summary>
internal sealed class RecordingConsole : ISystemConsole
{
    private readonly Queue<ConsoleKey> _keys = new();

    public List<string> Lines { get; } = [];
    public int ClearCount { get; private set; }
    public bool IsInputRedirected { get; set; }

    public bool KeyAvailable => _keys.Count > 0;

    public ConsoleKey ReadKey() => _keys.Dequeue();

    public void Clear() => ClearCount++;

    public void WriteLine(string value) => Lines.Add(value);

    public void EnqueueKey(ConsoleKey key) => _keys.Enqueue(key);
}

/// <summary>
/// Minimal <see cref="IProxyConfiguration"/>; only Output/IPAddress/ApiPort/Record
/// are read by the interactive console, the rest carry inert defaults.
/// </summary>
internal sealed class FakeProxyConfiguration : IProxyConfiguration
{
    public int ApiPort { get; set; } = 8897;
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
    public int Port { get; set; } = 8000;
    public bool Record { get; set; }
    public bool ShowTimestamps => false;
    public long? TimeoutSeconds { get; set; }
    public bool ValidateSchemas => false;
    public IEnumerable<int> WatchPids { get; set; } = [];
    public IEnumerable<string> WatchProcessNames { get; set; } = [];
}
