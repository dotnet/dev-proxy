// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocks;

internal class CrudApiDefinitionLoader(ILogger logger, CrudApiConfiguration configuration, bool validateSchemas) : BaseLoader(logger, validateSchemas)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly CrudApiConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    protected override string FilePath => _configuration.ApiFile;

    protected override void LoadData(string fileContents)
    {
        try
        {
            var apiDefinitionConfig = JsonSerializer.Deserialize<CrudApiConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
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
}
