// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.Mocks;

internal class CrudApiDefinitionLoader(ILogger logger, CrudApiConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly CrudApiConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private FileSystemWatcher? _watcher;

    public void LoadApiDefinition()
    {
        if (!File.Exists(_configuration.ApiFile))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(_configuration.ApiFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var apiDefinitionString = reader.ReadToEnd();
            var apiDefinitionConfig = JsonSerializer.Deserialize<CrudApiConfiguration>(apiDefinitionString, ProxyUtils.JsonSerializerOptions);
            _configuration.BaseUrl = apiDefinitionConfig?.BaseUrl ?? string.Empty;
            _configuration.DataFile = apiDefinitionConfig?.DataFile ?? string.Empty;
            _configuration.Auth = apiDefinitionConfig?.Auth ?? CrudApiAuthType.None;
            _configuration.EntraAuthConfig = apiDefinitionConfig?.EntraAuthConfig;

            IEnumerable<CrudApiAction>? configResponses = apiDefinitionConfig?.Actions;
            if (configResponses is not null)
            {
                _configuration.Actions = configResponses;
                foreach (var action in _configuration.Actions)
                {
                    if (string.IsNullOrEmpty(action.Method))
                    {
                        action.Method = action.Action switch
                        {
                            CrudApiActionType.Create => "POST",
                            CrudApiActionType.GetAll => "GET",
                            CrudApiActionType.GetOne => "GET",
                            CrudApiActionType.GetMany => "GET",
                            CrudApiActionType.Merge => "PATCH",
                            CrudApiActionType.Update => "PUT",
                            CrudApiActionType.Delete => "DELETE",
                            _ => throw new InvalidOperationException($"Unknown action type {action.Action}")
                        };
                    }
                }
                _logger.LogInformation("{configResponseCount} actions for CRUD API loaded from {apiFile}", configResponses.Count(), _configuration.ApiFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {apiFile}", _configuration.ApiFile);
        }
    }

    public void InitApiDefinitionWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(_configuration.ApiFile) ?? throw new InvalidOperationException($"{_configuration.ApiFile} is an invalid path");
        if (!File.Exists(_configuration.ApiFile))
        {
            _logger.LogWarning("File {configurationFile} not found. No CRUD API will be provided", _configuration.ApiFile);
            _configuration.Actions = [];
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(_configuration.ApiFile),
            EnableRaisingEvents = true
        };
        _watcher.Changed += ApiDefinitionFile_Changed;
        _watcher.Created += ApiDefinitionFile_Changed;
        _watcher.Deleted += ApiDefinitionFile_Changed;
        _watcher.Renamed += ApiDefinitionFile_Changed;

        LoadApiDefinition();
    }

    private void ApiDefinitionFile_Changed(object sender, FileSystemEventArgs e)
    {
        LoadApiDefinition();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
