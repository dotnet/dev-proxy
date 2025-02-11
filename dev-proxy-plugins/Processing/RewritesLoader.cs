// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Microsoft.DevProxy.Plugins.Processing;

internal class RewritesLoader(ILogger logger, RewritePluginConfiguration configuration) : IDisposable
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RewritePluginConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string RewritesFilePath => _configuration.RewritesFile;
    private FileSystemWatcher? _watcher;

    public void LoadRewrites()
    {
        if (!File.Exists(RewritesFilePath))
        {
            _logger.LogWarning("File {configurationFile} not found. No rewrites will be provided", _configuration.RewritesFile);
            _configuration.Rewrites = [];
            return;
        }

        try
        {
            using var stream = new FileStream(RewritesFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var RewritesString = reader.ReadToEnd();
            var rewritesConfig = JsonSerializer.Deserialize<RewritePluginConfiguration>(RewritesString, ProxyUtils.JsonSerializerOptions);
            IEnumerable<RequestRewrite>? configRewrites = rewritesConfig?.Rewrites;
            if (configRewrites is not null)
            {
                _configuration.Rewrites = configRewrites;
                _logger.LogInformation("Rewrites for {configResponseCount} url patterns loaded from {RewritesFile}", configRewrites.Count(), _configuration.RewritesFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {RewritesFile}:", _configuration.RewritesFile);
        }
    }

    public void InitResponsesWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        string path = Path.GetDirectoryName(RewritesFilePath) ?? throw new InvalidOperationException($"{RewritesFilePath} is an invalid path");
        if (!File.Exists(RewritesFilePath))
        {
            _logger.LogWarning("File {RewritesFile} not found. No rewrites will be provided", _configuration.RewritesFile);
            _configuration.Rewrites = [];
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetFullPath(path))
        {
            NotifyFilter = NotifyFilters.CreationTime
                             | NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            Filter = Path.GetFileName(RewritesFilePath)
        };
        _watcher.Changed += ResponsesFile_Changed;
        _watcher.Created += ResponsesFile_Changed;
        _watcher.Deleted += ResponsesFile_Changed;
        _watcher.Renamed += ResponsesFile_Changed;
        _watcher.EnableRaisingEvents = true;

        LoadRewrites();
    }

    private void ResponsesFile_Changed(object sender, FileSystemEventArgs e)
    {
        LoadRewrites();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
