// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Abstractions.Plugins;

public abstract class BaseLoader(HttpClient httpClient, ILogger logger, IProxyConfiguration proxyConfiguration) : IDisposable
{
    private CancellationToken? _cancellationToken;
#pragma warning disable CA2213 // HttpClient is injected, so we don't dispose it
    private readonly HttpClient _httpClient = httpClient;
#pragma warning restore CA2213
    private readonly bool _validateSchemas = proxyConfiguration.ValidateSchemas;
    private readonly Lock _debounceLock = new();
    private readonly int _debounceDelay = 300; // milliseconds
    private readonly Lock _loadLock = new();
    private int _loadGeneration;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private volatile bool _isDisposed;

    protected abstract string FilePath { get; }
    /// <summary>
    /// Whether to resolve environment variable placeholders (<c>${NAME}</c>, <c>${env:NAME}</c>)
    /// in string values of the loaded file. Defaults to true.
    /// </summary>
    protected virtual bool ResolveEnvironmentVariables => true;
    protected ILogger Logger { get; } = logger;

    public async Task InitFileWatcherAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        if (_watcher is not null)
        {
            return;
        }

        var path = Path.GetDirectoryName(FilePath) ?? throw new InvalidOperationException($"{FilePath} is an invalid path");
        if (!File.Exists(FilePath))
        {
            Logger.LogWarning("File {File} not found. No data will be provided", FilePath);
            return;
        }

        _watcher = new(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(FilePath)
        };
        _watcher.Changed += File_Changed;
        _watcher.EnableRaisingEvents = true;

        await LoadFileContentsAsync(cancellationToken);
    }

    protected abstract void LoadData(string fileContents);

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        // Set flag first to prevent in-flight timer callbacks from proceeding
        _isDisposed = true;

        if (disposing)
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
        }
    }

    private async Task<bool> ValidateFileContentsAsync(string fileContents, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(fileContents, ProxyUtils.JsonDocumentOptions);
        var root = document.RootElement;

        // Schema validation only applies to JSON objects, not arrays
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("$schema", out var schemaUrlElement))
        {
            Logger.LogDebug("Schema reference not found in file {File}. Skipping schema validation", FilePath);
            return true;
        }

        var schemaUrl = schemaUrlElement.GetString() ?? "";
        ProxyUtils.ValidateSchemaVersion(schemaUrl, Logger);
        var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync(fileContents, schemaUrl, _httpClient, Logger, cancellationToken);

        if (result.Status == SchemaValidationStatus.SchemaUnavailable)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("Skipping schema validation for {File} because the schema {SchemaUrl} could not be retrieved: {Error}", FilePath, schemaUrl, string.Join(", ", result.Errors));
            }
            return true;
        }

        if (result.Status == SchemaValidationStatus.Invalid)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                Logger.LogError("Schema validation failed for {File} with the following errors: {Errors}", FilePath, string.Join(", ", result.Errors));
            }
            return false;
        }

        return true;
    }

    private async Task LoadFileContentsAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!File.Exists(FilePath))
        {
            Logger.LogWarning("File {File} not found. No data will be loaded", FilePath);
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var fileContents = await reader.ReadToEndAsync(cancellationToken);

            // Convert YAML to JSON if needed
            string jsonContents;
            if (ProxyYaml.IsYamlFile(FilePath))
            {
                if (!ProxyYaml.TryConvertYamlToJson(fileContents, out var converted, out var error))
                {
                    Logger.LogError("Failed to parse YAML file {File}: {Error}", FilePath, error);
                    return;
                }
                jsonContents = converted!;
            }
            else
            {
                jsonContents = fileContents;
            }

            if (ResolveEnvironmentVariables)
            {
                var unresolvedVariables = new List<string>();
                jsonContents = EnvironmentVariablePlaceholders.ResolveInJson(jsonContents, unresolvedVariables);
                EnvironmentVariablePlaceholders.LogUnresolvedVariables(Logger, unresolvedVariables, FilePath);
            }

            if (!_validateSchemas || await ValidateFileContentsAsync(jsonContents, cancellationToken))
            {
                lock (_loadLock)
                {
                    // A newer load started while this one was validating; don't
                    // let this older snapshot overwrite the newer data
                    if (generation != Volatile.Read(ref _loadGeneration))
                    {
                        Logger.LogDebug("Skipping superseded load of {File}", FilePath);
                        return;
                    }

                    LoadData(jsonContents);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug("Loading {File} was canceled", FilePath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {File}:", FilePath);
        }
    }

    private void File_Changed(object sender, FileSystemEventArgs e)
    {
        // Don't process file changes if this loader has been disposed
        if (_isDisposed)
        {
            return;
        }

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
#pragma warning disable CS4014 // we don't need to await this
            _debounceTimer = new(_ => LoadFileContentsAsync(_cancellationToken ?? CancellationToken.None), null, _debounceDelay, Timeout.Infinite);
#pragma warning restore CS4014
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}