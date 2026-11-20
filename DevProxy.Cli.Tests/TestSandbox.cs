// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Per-test working folder with a Dev Proxy config that loads a mock plugin, plus
/// environment overrides that keep the run away from the user's real Dev Proxy data
/// where the OS allows it (HOME/XDG_CONFIG_HOME on Linux, root certificate path on
/// every OS). Instances started through <see cref="TrackInstance"/> are stopped by
/// PID on dispose, so a test never stops proxies it didn't start.
/// </summary>
sealed class TestSandbox : IAsyncDisposable
{
    public const string MockedUrl = "http://api.contoso.local/hello";
    public const string MockedBody = "hello from the Dev Proxy CLI scenario tests";
    // Logged by MockResponsePlugin when it loads; must never show up in subcommand output.
    public const string PluginLoadedMessage = "Mock responses for";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("devproxy-cli-");
    private readonly DateTime _createdUtc = DateTime.UtcNow.AddSeconds(-1);
    private readonly List<int> _pids = [];
    private readonly List<string> _logFiles = [];

    public TestSandbox()
    {
        WorkDir = _root.CreateSubdirectory("work").FullName;
        var home = _root.CreateSubdirectory("home").FullName;
        Environment = new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
            ["DEV_PROXY_CERT_PATH"] = Path.Combine(home, "rootCert.pfx"),
        };

        File.WriteAllText(Path.Combine(WorkDir, "devproxyrc.json"), $$"""
            {
              "plugins": [
                {
                  "name": "MockResponsePlugin",
                  "enabled": true,
                  "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
                  "configSection": "mocksPlugin"
                }
              ],
              "urlsToWatch": [
                "http://api.contoso.local/*"
              ],
              "mocksPlugin": {
                "mocksFile": "mocks.json"
              },
              "newVersionNotification": "none",
              "validateSchemas": false
            }
            """);
        File.WriteAllText(Path.Combine(WorkDir, "mocks.json"), $$"""
            {
              "mocks": [
                {
                  "request": { "url": "{{MockedUrl}}" },
                  "response": { "statusCode": 200, "body": "{{MockedBody}}" }
                }
              ]
            }
            """);
    }

    public string WorkDir { get; }

    /// <summary>The config file path below the unique, per-test temp folder.</summary>
    public string RelativeConfigPath => Path.Combine(_root.Name, "work", "devproxyrc.json");

    public IReadOnlyDictionary<string, string> Environment { get; }

    public void TrackInstance(int pid, string? logFile)
    {
        _pids.Add(pid);
        if (!string.IsNullOrEmpty(logFile))
        {
            _logFiles.Add(logFile);
        }
    }

    public static string Pid(int pid) => pid.ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        foreach (var pid in _pids)
        {
            try
            {
                var status = await DevProxyCli.RunAsync(this, "status", "--pid", Pid(pid));
                if (status.ExitCode == 0)
                {
                    _ = await DevProxyCli.RunAsync(this, "stop", "--pid", Pid(pid));
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
            }
        }

        foreach (var logFile in _logFiles)
        {
            TryDelete(() => File.Delete(logFile));
        }
        DeleteUntrackedLogs();
        TryDelete(() => _root.Delete(recursive: true));
    }

    // A detached start that fails before reporting its PID still writes a log to the
    // shared (non-sandboxed on macOS/Windows) logs folder. Remove the ones that reference
    // this sandbox so the tests leave no trace in the user's Dev Proxy folder.
    private void DeleteUntrackedLogs()
    {
        var basePath = OperatingSystem.IsWindows()
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var logsDir = new DirectoryInfo(Path.Combine(basePath, "dev-proxy", "logs"));
        if (!logsDir.Exists)
        {
            return;
        }

        foreach (var log in logsDir.EnumerateFiles("devproxy-*.log"))
        {
            if (log.LastWriteTimeUtc < _createdUtc)
            {
                continue;
            }
            TryDelete(() =>
            {
                if (File.ReadAllText(log.FullName).Contains(_root.Name, StringComparison.Ordinal))
                {
                    log.Delete();
                }
            });
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
