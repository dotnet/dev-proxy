// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Mocking;

internal sealed class CrudApiDefinitionLoader(
    HttpClient httpClient,
    ILogger<CrudApiDefinitionLoader> logger,
    CrudApiConfiguration configuration,
    IProxyConfiguration proxyConfiguration) :
    BaseLoader(httpClient, logger, proxyConfiguration)
{
    private readonly CrudApiConfiguration _configuration = configuration;

    protected override string FilePath => _configuration.ApiFile;

    protected override void LoadData(Stream fileContents)
    {
        try
        {
            var apiDefinitionConfig = JsonSerializer.Deserialize<CrudApiConfiguration>(fileContents, ProxyUtils.JsonSerializerOptions);
            // Removing null entries from roles/scopes that contain only nulls would
            // leave them empty, which disables the permission check. Reject such
            // files instead of silently allowing any token.
            var invalidPermissions = GetPermissionsWithOnlyNullEntries(apiDefinitionConfig).ToArray();
            if (invalidPermissions.Length > 0)
            {
                Logger.LogError(
                    "{ApiFile} contains permissions with only null entries: {Permissions}. Specify at least one role or scope, or remove the property. API definition not loaded",
                    _configuration.ApiFile,
                    string.Join(", ", invalidPermissions));
                return;
            }
            RemoveNullEntries(apiDefinitionConfig);
            _configuration.BaseUrl = apiDefinitionConfig?.BaseUrl ?? string.Empty;
            _configuration.DataFile = apiDefinitionConfig?.DataFile ?? string.Empty;
            _configuration.Auth = apiDefinitionConfig?.Auth ?? CrudApiAuthType.None;
            _configuration.EntraAuthConfig = apiDefinitionConfig?.EntraAuthConfig;
            _configuration.ApiKeyAuthConfig = apiDefinitionConfig?.ApiKeyAuthConfig;

            var configResponses = apiDefinitionConfig?.Actions;
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
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation("{ConfigResponseCount} actions for CRUD API loaded from {ApiFile}", configResponses.Count(), _configuration.ApiFile);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {ApiFile}", _configuration.ApiFile);
        }
    }

    private static IEnumerable<string> GetPermissionsWithOnlyNullEntries(CrudApiConfiguration? config)
    {
        if (config is null)
        {
            return [];
        }

        var authConfigs = new List<(string Path, CrudApiEntraAuth? AuthConfig)> { ("entraAuthConfig", config.EntraAuthConfig) };
        authConfigs.AddRange((config.Actions ?? []).Select((a, i) => ($"actions[{i}].entraAuthConfig", a?.EntraAuthConfig)));

        return authConfigs
            .Where(c => c.AuthConfig is not null)
            .SelectMany(c => new[]
            {
                (Path: $"{c.Path}.roles", Values: c.AuthConfig!.Roles),
                (Path: $"{c.Path}.scopes", Values: c.AuthConfig!.Scopes)
            })
            .Where(p => p.Values is not null && p.Values.Any() && p.Values.All(v => v is null))
            .Select(p => p.Path);
    }
}
