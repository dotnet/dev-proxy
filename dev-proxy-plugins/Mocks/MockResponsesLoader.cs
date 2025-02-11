// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.Mocks;

internal class MockResponsesLoader(ILogger logger, MockResponseConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MockResponseConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string ResponsesFilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.MocksFile);
    private FileSystemWatcher? _watcher;

    public void LoadResponses()
    {
        if (!File.Exists(ResponsesFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No mocks will be provided", _configuration.MocksFile);
            _configuration.Mocks = [];
            return;
        }

        try
        {
            using var stream = new FileStream(ResponsesFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var responsesString = reader.ReadToEnd();
            var responsesConfig = JsonSerializer.Deserialize<MockResponseConfiguration>(responsesString, ProxyUtils.JsonSerializerOptions);
            IEnumerable<MockResponse>? configResponses = responsesConfig?.Mocks;
            if (configResponses is not null)
            {
                _configuration.Mocks = configResponses;
                _logger.LogInformation("Mock responses for {configResponseCount} url patterns loaded from {mockFile}", configResponses.Count(), _configuration.MocksFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.MocksFile);
        }
    }

    public void InitResponsesWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(ResponsesFilePath) ?? throw new InvalidOperationException($"{ResponsesFilePath} is an invalid path");
        if (!File.Exists(ResponsesFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No mocks will be provided", _configuration.MocksFile);
            _configuration.Mocks = [];
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path));
        _watcher.NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size;
        _watcher.Filter = Path.GetFileName(ResponsesFilePath);
        _watcher.Changed += ResponsesFile_Changed;
        _watcher.Created += ResponsesFile_Changed;
        _watcher.Deleted += ResponsesFile_Changed;
        _watcher.Renamed += ResponsesFile_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadResponses();
    }

    private void ResponsesFile_Changed(object sender, FileSystemEventArgs e)
    {
        LoadResponses();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
