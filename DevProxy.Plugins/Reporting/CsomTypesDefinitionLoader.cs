// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Reporting;

internal sealed class CsomTypesDefinitionLoader(
    ILogger<CsomTypesDefinitionLoader> logger,
    MinimalCsomPermissionsPluginConfiguration configuration,
    IProxyConfiguration proxyConfiguration) : BaseLoader(logger, proxyConfiguration)
{
    private readonly ILogger _logger = logger;
    private readonly MinimalCsomPermissionsPluginConfiguration _configuration = configuration;

    protected override string FilePath => _configuration.TypesFilePath!;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var types = JsonSerializer.Deserialize<CsomTypesDefinition>(fileContents, ProxyUtils.JsonSerializerOptions);
            if (types is not null)
            {
                _configuration.TypesDefinitions = types;
                _logger.LogInformation("CSOM types definitions loaded from {File}", _configuration.TypesFilePath);
            }
            else
            {
                _configuration.TypesDefinitions = new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.TypesFilePath);
        }
    }
}
