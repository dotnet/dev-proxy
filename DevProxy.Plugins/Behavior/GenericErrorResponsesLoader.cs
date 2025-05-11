// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Behavior;

internal sealed class GenericErrorResponsesLoader(
    ILogger<GenericErrorResponsesLoader> logger,
    GenericRandomErrorConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(logger, proxyConfiguration)
{
    private readonly ILogger _logger = logger;
    private readonly GenericRandomErrorConfiguration _configuration = configuration;

    protected override string FilePath => _configuration.ErrorsFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var responsesConfig = JsonSerializer.Deserialize<GenericRandomErrorConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            var configResponses = responsesConfig?.Errors;
            if (configResponses is not null)
            {
                _configuration.Errors = configResponses;
                _logger.LogInformation("{ConfigResponseCount} error responses loaded from {ErrorFile}", configResponses.Count(), _configuration.ErrorsFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.ErrorsFile);
        }
    }
}
