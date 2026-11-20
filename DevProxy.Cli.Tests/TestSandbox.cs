// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace DevProxy.Cli.Tests;

/// <summary>
/// Per-test working folder with a Dev Proxy config that loads a mock plugin, plus
/// environment overrides that keep the run away from the user's real Dev Proxy data:
/// <c>DEV_PROXY_CONFIG_DIR</c> moves state, logs and certificates into the sandbox on
/// every OS. Instances started through <see cref="TrackInstance"/> are stopped by
/// PID on dispose, so a test never stops proxies it didn't start.
/// </summary>
sealed class TestSandbox : IAsyncDisposable
{
    public const string MockedUrl = "http://api.contoso.local/hello";
    public const string MockedBody = "hello from the Dev Proxy CLI scenario tests";
    // Logged by MockResponsePlugin when it loads; must never show up in subcommand output.
    public const string PluginLoadedMessage = "Mock responses for";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("devproxy-cli-");
    private readonly List<int> _pids = [];

    public TestSandbox()
    {
        WorkDir = _root.CreateSubdirectory("work").FullName;
        var home = _root.CreateSubdirectory("home").FullName;
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
            ["DEV_PROXY_CONFIG_DIR"] = Path.Combine(_root.FullName, "data"),
        };
        // Keep update checks and announcements off the network: they fail fast through
        // this unreachable proxy. Loopback stays direct for the Dev Proxy API.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
        {
            environment[name] = "http://127.0.0.1:9";
        }
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
        {
            environment[name] = "localhost,127.0.0.1,::1";
        }
        Environment = environment;

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

    /// <summary>Dev Proxy's state, logs and certificates for this sandbox.</summary>
    public string DataDir => Environment["DEV_PROXY_CONFIG_DIR"];

    public void TrackInstance(int pid) => _pids.Add(pid);

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
                    // --force doesn't depend on the API shutdown that the tests exercise.
                    _ = await DevProxyCli.RunAsync(this, "stop", "--force", "--pid", Pid(pid));
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
            }
        }

        TryDelete(() => _root.Delete(recursive: true));
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
