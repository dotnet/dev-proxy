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
    HttpClient httpClient,
    ILogger<CsomTypesDefinitionLoader> logger,
    MinimalCsomPermissionsPluginConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
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
                Logger.LogInformation("CSOM types definitions loaded from {File}", _configuration.TypesFilePath);
            }
            else
            {
                _configuration.TypesDefinitions = new();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {ConfigurationFile}:", _configuration.TypesFilePath);
        }
    }
}
