// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Manipulation;

internal sealed class RewritesLoader(
    ILogger<RewritesLoader> logger,
    RewritePluginConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(logger, proxyConfiguration)
{
    private readonly RewritePluginConfiguration _configuration = configuration;
    private readonly ILogger _logger = logger;

    protected override string FilePath => _configuration.RewritesFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var rewritesConfig = JsonSerializer.Deserialize<RewritePluginConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var configRewrites = rewritesConfig?.Rewrites;
            if (configRewrites is not null)
            {
                _configuration.Rewrites = configRewrites;
                _logger.LogInformation("Rewrites for {ConfigResponseCount} url patterns loaded from {RewritesFile}", configRewrites.Count(), _configuration.RewritesFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {RewritesFile}:", _configuration.RewritesFile);
        }
    }
}
