// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Processing;

internal class RewritesLoader(ILogger logger, RewritePluginConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RewritePluginConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => _configuration.RewritesFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var rewritesConfig = JsonSerializer.Deserialize<RewritePluginConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
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
}
