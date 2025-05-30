// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Abstractions;

public abstract class BaseLoader(ILogger logger, bool validateSchemas) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly bool _validateSchemas = validateSchemas;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly Lock _debounceLock = new();
    private readonly int _debounceDelay = 300; // milliseconds
    protected abstract string FilePath { get; }

    protected abstract void LoadData(string fileContents);

    private async Task<bool> ValidateFileContents(string fileContents)
    {
        using var document = JsonDocument.Parse(fileContents, ProxyUtils.JsonDocumentOptions);
        var root = document.RootElement;

        if (!root.TryGetProperty("$schema", out var schemaUrlElement))
        {
            _logger.LogDebug("Schema reference not found in file {File}. Skipping schema validation", FilePath);
            return true;
        }

        var schemaUrl = schemaUrlElement.GetString() ?? "";
        ProxyUtils.ValidateSchemaVersion(schemaUrl, _logger);
        var (IsValid, ValidationErrors) = await ProxyUtils.ValidateJson(fileContents, schemaUrl, _logger);

        if (!IsValid)
        {
            _logger.LogError("Schema validation failed for {File} with the following errors: {Errors}", FilePath, string.Join(", ", ValidationErrors));
        }

        return IsValid;
    }

    private void LoadFileContents()
    {
        if (!File.Exists(FilePath))
        {
            _logger.LogWarning("File {File} not found. No data will be loaded", FilePath);
            return;
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var responsesString = reader.ReadToEnd();

            if (!_validateSchemas || ValidateFileContents(responsesString).Result)
            {
                LoadData(responsesString);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {File}:", FilePath);
        }
    }

    private void File_Changed(object sender, FileSystemEventArgs e)
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => LoadFileContents(), null, _debounceDelay, Timeout.Infinite);
        }
    }

    public void InitFileWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(FilePath) ?? throw new InvalidOperationException($"{FilePath} is an invalid path");
        if (!File.Exists(FilePath))
        {
            _logger.LogWarning("File {File} not found. No data will be provided", FilePath);
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(FilePath)
        };
        _watcher.Changed += File_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadFileContents();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}