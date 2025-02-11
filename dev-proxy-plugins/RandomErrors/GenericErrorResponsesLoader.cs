// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.RandomErrors;

internal class GenericErrorResponsesLoader(ILogger logger, GenericRandomErrorConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly GenericRandomErrorConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string ErrorsFile => Path.Combine(Directory.GetCurrentDirectory(), _configuration.ErrorsFile ?? "");
    private FileSystemWatcher? _watcher;

    public void LoadResponses()
    {
        if (!File.Exists(ErrorsFile))
        {
            _logger.LogWarning("File {configurationFile} not found in the current directory. No error responses will be loaded", _configuration.ErrorsFile);
            _configuration.Errors = [];
            return;
        }

        try
        {
            using var stream = new FileStream(ErrorsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var responsesString = reader.ReadToEnd();
            var responsesConfig = JsonSerializer.Deserialize<GenericRandomErrorConfiguration>(responsesString, ProxyUtils.JsonSerializerOptions);
            IEnumerable<GenericErrorResponse>? configResponses = responsesConfig?.Errors;
            if (configResponses is not null)
            {
                _configuration.Errors = configResponses;
                _logger.LogInformation("{configResponseCount} error responses loaded from {errorFile}", configResponses.Count(), _configuration.ErrorsFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.ErrorsFile);
        }
    }

    public void InitResponsesWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(ErrorsFile) ?? throw new InvalidOperationException($"{ErrorsFile} is an invalid path");
        _watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(ErrorsFile)
        };
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
