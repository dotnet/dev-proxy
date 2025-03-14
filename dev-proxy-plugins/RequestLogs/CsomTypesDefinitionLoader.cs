// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.RequestLogs;

internal class CsomTypesDefinitionLoader(ILogger logger, MinimalCsomPermissionsPluginConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MinimalCsomPermissionsPluginConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => Path.Combine(Directory.GetCurrentDirectory(), _configuration.TypesFilePath!);

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
                _configuration.TypesDefinitions = new CsomTypesDefinition();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error has occurred while reading {configurationFile}:", _configuration.TypesFilePath);
        }
    }
}
