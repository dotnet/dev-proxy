// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.Mocks;

internal class MockRequestLoader(ILogger logger, MockRequestConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MockRequestConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string RequestFilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.MockFile);
    private FileSystemWatcher? _watcher;

    public void LoadRequest()
    {
        if (!File.Exists(RequestFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No mocks request will be issued", _configuration.MockFile);
            _configuration.Request = null;
            return;
        }

        try
        {
            using var stream = new FileStream(RequestFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var requestString = reader.ReadToEnd();
            var requestConfig = JsonSerializer.Deserialize<MockRequestConfiguration>(requestString, ProxyUtils.JsonSerializerOptions);
            var configRequest = requestConfig?.Request;
            if (configRequest is not null)
            {
                _configuration.Request = configRequest;
                _logger.LogInformation("Mock request to url {url} loaded from {mockFile}", _configuration.Request.Url, _configuration.MockFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.MockFile);
        }
    }

    public void InitResponsesWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(RequestFilePath) ?? throw new InvalidOperationException($"{RequestFilePath} is an invalid path");
        if (!File.Exists(RequestFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No mock request will be issued", _configuration.MockFile);
            _configuration.Request = null;
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(RequestFilePath)
        };
        _watcher.Changed += RequestFile_Changed;
        _watcher.Created += RequestFile_Changed;
        _watcher.Deleted += RequestFile_Changed;
        _watcher.Renamed += RequestFile_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadRequest();
    }

    private void RequestFile_Changed(object sender, FileSystemEventArgs e)
    {
        LoadRequest();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
