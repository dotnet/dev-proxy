// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.Behavior;

internal class RateLimitingCustomResponseLoader(ILogger logger, RateLimitConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RateLimitConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string CustomResponseFilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.CustomResponseFile);
    private FileSystemWatcher? _watcher;

    public void LoadResponse()
    {
        if (!File.Exists(CustomResponseFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No response will be provided", _configuration.CustomResponseFile);
            return;
        }

        try
        {
            using var stream = new FileStream(CustomResponseFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var responseString = reader.ReadToEnd();
            var response = JsonSerializer.Deserialize<MockResponseResponse>(responseString, ProxyUtils.JsonSerializerOptions);
            if (response is not null)
            {
                _configuration.CustomResponse = response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.CustomResponseFile);
        }
    }

    public void InitResponsesWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(CustomResponseFilePath) ?? throw new InvalidOperationException($"{CustomResponseFilePath} is an invalid path");
        if (!File.Exists(CustomResponseFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No mocks will be provided", _configuration.CustomResponseFile);
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(CustomResponseFilePath)
        };
        _watcher.Changed += ResponseFile_Changed;
        _watcher.Created += ResponseFile_Changed;
        _watcher.Deleted += ResponseFile_Changed;
        _watcher.Renamed += ResponseFile_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadResponse();
    }

    private void ResponseFile_Changed(object sender, FileSystemEventArgs e)
    {
        LoadResponse();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
