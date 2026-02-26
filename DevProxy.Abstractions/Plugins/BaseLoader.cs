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

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private volatile bool _isDisposed;

    protected abstract string FilePath { get; }
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
        var (IsValid, ValidationErrors) = await ProxyUtils.ValidateJsonAsync(fileContents, schemaUrl, _httpClient, Logger, cancellationToken);

        if (!IsValid)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                Logger.LogError("Schema validation failed for {File} with the following errors: {Errors}", FilePath, string.Join(", ", ValidationErrors));
            }
        }

        return IsValid;
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

            if (!_validateSchemas || await ValidateFileContentsAsync(jsonContents, cancellationToken))
            {
                LoadData(jsonContents);
            }
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