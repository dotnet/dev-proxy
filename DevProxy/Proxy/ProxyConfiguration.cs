// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using System.Text.Json.Serialization;

namespace DevProxy.Proxy;

sealed class ProxyConfiguration : IProxyConfiguration, IProcessTreeProxyConfiguration
{
    private readonly IConfigurationRoot _configurationRoot;

    public int ApiPort { get; set; } = 8897;
    public string ApiIpAddress { get; set; } = "127.0.0.1";
    public bool AsSystemProxy { get; set; } = true;
    private bool configFileResolved;
    private string configFile = string.Empty;
    public string ConfigFile
    {
        get
        {
            if (configFileResolved)
            {
                return configFile;
            }
            configFile = GetConfigFilePath(_configurationRoot);
            configFileResolved = true;
            return configFile;
        }
    }
    public Dictionary<string, string> Env { get; set; } = [];
    public IEnumerable<MockRequestHeader>? FilterByHeaders { get; set; }
    public string? IPAddress { get; set; } = "127.0.0.1";
    public bool InstallCert { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OutputFormat Output { get; set; } = OutputFormat.Text;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool NoFirstRun { get; set; }
    public bool NoWatch { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReleaseType NewVersionNotification { get; set; } = ReleaseType.Stable;
    public int Port { get; set; } = 8000;
    public bool Record { get; set; }
    public bool ShowSkipMessages { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public long? TimeoutSeconds { get; set; }
    internal List<string> UrlsToWatch { get; set; } = [];
    public bool ValidateSchemas { get; set; } = true;
    public IEnumerable<int> WatchPids { get; set; } = [];
    public IEnumerable<string> WatchProcessNames { get; set; } = [];
    public bool WatchProcessTree { get; set; }

    // True when the value wasn't set in the config file and was turned off
    // because Dev Proxy is running in CI.
    internal bool AsSystemProxyDefaultedForCi { get; private set; }
    internal bool InstallCertDefaultedForCi { get; private set; }

    internal static bool IsCi => Environment.GetEnvironmentVariable("CI") is not null;

    public ProxyConfiguration(IConfigurationRoot configurationRoot) : this(configurationRoot, IsCi)
    {
    }

    internal ProxyConfiguration(IConfigurationRoot configurationRoot, bool isCi)
    {
        _configurationRoot = configurationRoot;
        _configurationRoot.Bind(this);
        if (isCi)
        {
            ApplyCiDefaults(_configurationRoot);
        }
    }

    // In CI, don't change machine-wide state (system proxy, trusted root
    // certificates) unless the config file explicitly asks for it.
    private void ApplyCiDefaults(IConfiguration configuration)
    {
        if (configuration[nameof(AsSystemProxy)] is null)
        {
            AsSystemProxy = false;
            AsSystemProxyDefaultedForCi = true;
        }
        if (configuration[nameof(InstallCert)] is null)
        {
            InstallCert = false;
            InstallCertDefaultedForCi = true;
        }
    }

    internal static string GetConfigFilePath(IConfigurationRoot configurationRoot)
    {
        if (configurationRoot.Providers.FirstOrDefault(p => p is FileConfigurationProvider) is not FileConfigurationProvider provider)
        {
            throw new InvalidOperationException("Unable to resolve config file path.");
        }

        var fileInfo = provider.Source.FileProvider!.GetFileInfo(provider.Source.Path!);
        return fileInfo.PhysicalPath!;
    }
}