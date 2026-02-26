// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy;

sealed class ConfigFileWatcher(
    IProxyConfiguration proxyConfiguration,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<ConfigFileWatcher> logger) : IHostedService, IDisposable
{
    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;
    private readonly IHostApplicationLifetime _hostApplicationLifetime = hostApplicationLifetime;
    private readonly ILogger<ConfigFileWatcher> _logger = logger;
    private FileSystemWatcher? _watcher;
    private DateTime _lastReloadTime = DateTime.MinValue;
    private static readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Indicates whether the application is restarting due to a configuration change.
    /// </summary>
    public static bool IsRestarting { get; private set; }

    /// <summary>
    /// Signaled when the proxy has fully stopped and system proxy is deregistered.
    /// </summary>
    public static TaskCompletionSource? ProxyStoppedCompletionSource { get; private set; }

    /// <summary>
    /// Resets the restart flag. Called before each proxy run.
    /// </summary>
    public static void Reset()
    {
        IsRestarting = false;
        ProxyStoppedCompletionSource = null;
    }

    /// <summary>
    /// Signals that the proxy has fully stopped.
    /// </summary>
    public static void SignalProxyStopped() => ProxyStoppedCompletionSource?.TrySetResult();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_proxyConfiguration.NoWatch)
        {
            _logger.LogDebug("Configuration file watching is disabled");
            return Task.CompletedTask;
        }

        var configFilePath = _proxyConfiguration.ConfigFile;
        if (string.IsNullOrEmpty(configFilePath) || !File.Exists(configFilePath))
        {
            _logger.LogWarning("Configuration file not found, hot reload disabled");
            return Task.CompletedTask;
        }

        var directory = Path.GetDirectoryName(configFilePath);
        var fileName = Path.GetFileName(configFilePath);

        if (string.IsNullOrEmpty(directory))
        {
            _logger.LogWarning("Could not determine configuration file directory, hot reload disabled");
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnConfigFileChanged;

        _logger.LogDebug("Watching configuration file for changes: {ConfigFile}", configFilePath);

        return Task.CompletedTask;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce multiple rapid file system events
        var now = DateTime.UtcNow;
        if (now - _lastReloadTime < _debounceInterval)
        {
            return;
        }
        _lastReloadTime = now;

        try
        {
            _logger.LogInformation("Configuration file changed. Restarting proxy...");
            IsRestarting = true;
            ProxyStoppedCompletionSource = new TaskCompletionSource();
            _hostApplicationLifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while handling configuration file change. Restart has not been initiated.");
            IsRestarting = false;
            ProxyStoppedCompletionSource = null;
        }
    }

    private void DisposeWatcher()
    {
        var watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        watcher.Dispose();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatcher();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeWatcher();
    }
}
