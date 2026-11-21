// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// Loads the data from the file contents as a JSON string. YAML files are
    /// converted to JSON first. Override this method or <see cref="LoadData(Stream)"/>.
    /// </summary>
    protected virtual void LoadData(string fileContents) =>
        throw new NotImplementedException($"Override {nameof(LoadData)}(string) or {nameof(LoadData)}(Stream) in {GetType().Name}");

    /// <summary>
    /// Loads the data from the file contents as a UTF-8 JSON stream. YAML files
    /// are converted to JSON first. Override this method instead of
    /// <see cref="LoadData(string)"/> to avoid materializing the whole file
    /// as a string. The default implementation reads the stream into a string
    /// and calls <see cref="LoadData(string)"/>. The stream is disposed after
    /// this method returns.
    /// </summary>
    protected virtual void LoadData(Stream fileContents)
    {
        using var reader = new StreamReader(fileContents, leaveOpen: true);
        LoadData(reader.ReadToEnd());
    }

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

    private async Task<bool> ValidateFileContentsAsync(Stream fileContents, SchemaValidationCache.FileVersion? fileVersion, CancellationToken cancellationToken)
    {
        var schemaUrl = ProxyUtils.ReadSchemaReference(fileContents);
        fileContents.Position = 0;

        if (string.IsNullOrEmpty(schemaUrl))
        {
            Logger.LogDebug("Schema reference not found in file {File}. Skipping schema validation", FilePath);
            return true;
        }

        ProxyUtils.ValidateSchemaVersion(schemaUrl, Logger);

        var cacheKey = SchemaValidationCache.CreateKey(FilePath, schemaUrl);
        if (SchemaValidationCache.HasPassed(cacheKey, fileVersion))
        {
            Logger.LogDebug("File {File} hasn't changed since it passed schema validation. Skipping schema validation", FilePath);
            return true;
        }

        var (IsValid, ValidationErrors) = await ProxyUtils.ValidateJsonAsync(fileContents, schemaUrl, _httpClient, Logger, cancellationToken);
        fileContents.Position = 0;
        SchemaValidationCache.SetResult(cacheKey, fileVersion, IsValid);

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
            // read before the contents so that a change while loading isn't missed
            var fileVersion = SchemaValidationCache.GetFileVersion(FilePath);

            (string Json, string? ContentHash)? jsonContents;
            // validate and load the same snapshot, so that a change to the file while
            // loading can't publish contents that weren't validated
            using (var snapshot = await ReadFileAsync(cancellationToken))
            {
                if (!ProxyYaml.IsYamlFile(FilePath) && CanLoadAsIs(snapshot.GetBuffer().AsSpan(0, (int)snapshot.Length)))
                {
                    await LoadJsonAsync(snapshot, fileVersion, cancellationToken);
                    return;
                }

                jsonContents = await ReadJsonContentsAsync(snapshot, cancellationToken);
            }

            if (jsonContents is null)
            {
                return;
            }

            using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonContents.Value.Json), writable: false);
            await LoadJsonAsync(jsonStream, fileVersion is { } version ? version with { ContentHash = jsonContents.Value.ContentHash } : null, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {File}:", FilePath);
        }
    }

    private async Task LoadJsonAsync(Stream jsonContents, SchemaValidationCache.FileVersion? fileVersion, CancellationToken cancellationToken)
    {
        if (!_validateSchemas || await ValidateFileContentsAsync(jsonContents, fileVersion, cancellationToken))
        {
            LoadData(jsonContents);
        }
    }

    /// <summary>
    /// Reads the file as a string, converts YAML to JSON, and resolves environment variable placeholders.
    /// </summary>
    /// <returns>The JSON and, if placeholders changed the contents, a hash of the resolved JSON.
    /// <c>null</c> if the YAML couldn't be converted.</returns>
    private async Task<(string Json, string? ContentHash)?> ReadJsonContentsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var fileContents = await reader.ReadToEndAsync(cancellationToken);

        // Convert YAML to JSON if needed
        string jsonContents;
        if (ProxyYaml.IsYamlFile(FilePath))
        {
            if (!ProxyYaml.TryConvertYamlToJson(fileContents, out var converted, out var error))
            {
                Logger.LogError("Failed to parse YAML file {File}: {Error}", FilePath, error);
                return null;
            }
            jsonContents = converted!;
        }
        else
        {
            jsonContents = fileContents;
        }

        string? contentHash = null;
        if (ResolveEnvironmentVariables)
        {
            var unresolvedVariables = new List<string>();
            var resolvedContents = EnvironmentVariablePlaceholders.ResolveInJson(jsonContents, unresolvedVariables);
            EnvironmentVariablePlaceholders.LogUnresolvedVariables(Logger, unresolvedVariables, FilePath);

            // the validated contents depend on environment variables, not only on the file
            if (!ReferenceEquals(resolvedContents, jsonContents))
            {
                contentHash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(resolvedContents.AsSpan())));
                jsonContents = resolvedContents;
            }
        }

        return (jsonContents, contentHash);
    }

    private async Task<MemoryStream> ReadFileAsync(CancellationToken cancellationToken)
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
        var snapshot = new MemoryStream((int)Math.Min(stream.Length, Array.MaxLength));
        await stream.CopyToAsync(snapshot, cancellationToken);
        snapshot.Position = 0;
        return snapshot;
    }

    /// <summary>
    /// Returns true if the JSON file can be loaded as-is, without reading it into a string:
    /// it's not UTF-16 or UTF-32 encoded, and (if placeholders are resolved) it doesn't
    /// contain anything that could be a placeholder (<c>${</c> or a <c>\u</c> escape).
    /// </summary>
    private bool CanLoadAsIs(ReadOnlySpan<byte> fileContents)
    {
        if (fileContents.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ||
            fileContents.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ||
            fileContents.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return false;
        }

        if (!ResolveEnvironmentVariables)
        {
            return true;
        }

        byte previous = 0;
        foreach (var current in fileContents)
        {
            if ((previous == (byte)'$' && current == (byte)'{') ||
                (previous == (byte)'\\' && current == (byte)'u'))
            {
                return false;
            }
            // an escaped backslash can't start an escape sequence
            previous = previous == (byte)'\\' && current == (byte)'\\' ? (byte)0 : current;
        }

        return true;
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