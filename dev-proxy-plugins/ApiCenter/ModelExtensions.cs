// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Readers;

namespace Microsoft.DevProxy.Plugins.ApiCenter;

public static class ModelExtensions
{
    internal static async Task LoadDeploymentsAsync(this Api api, ApiCenterClient apiCenterClient)
    {
        if (api.Deployments is not null)
        {
            return;
        }

        Debug.Assert(api.Id is not null);

        var deployments = await apiCenterClient.GetDeploymentsAsync(api.Id);
        api.Deployments = deployments ?? [];
    }

    internal static async Task LoadVersionsAsync(this Api api, ApiCenterClient apiCenterClient)
    {
        if (api.Versions is not null)
        {
            return;
        }

        Debug.Assert(api.Id is not null);

        var versions = await apiCenterClient.GetVersionsAsync(api.Id);
        api.Versions = versions ?? [];
    }

    internal static async Task LoadDefinitionsAsync(this ApiVersion version, ApiCenterClient apiCenterClient)
    {
        if (version.Definitions is not null)
        {
            return;
        }

        Debug.Assert(version.Id is not null);

        var definitions = await apiCenterClient.GetDefinitionsAsync(version.Id);
        version.Definitions = definitions ?? [];
    }

    internal static ApiVersion? GetVersion(this Api api, RequestLog request, string requestUrl, ILogger logger)
    {
        if (api.Versions is null ||
            api.Versions.Length == 0)
        {
            return null;
        }

        if (api.Versions.Length == 1)
        {
            logger.LogDebug("API {api} has only one version {version}. Returning", api.Name, api.Versions[0].Name);
            return api.Versions[0];
        }

        // determine version based on:
        // - URL path and query parameters
        // - headers
        foreach (var apiVersion in api.Versions)
        {
            // check URL
            if (!string.IsNullOrEmpty(apiVersion.Name) &&
                requestUrl.Contains(apiVersion.Name))
            {
                logger.LogDebug("Version {version} found in URL {url}", apiVersion.Name, requestUrl);
                return apiVersion;
            }

            if (!string.IsNullOrEmpty(apiVersion.Properties?.Title) &&
                requestUrl.Contains(apiVersion.Properties.Title))
            {
                logger.LogDebug("Version {version} found in URL {url}", apiVersion.Properties.Title, requestUrl);
                return apiVersion;
            }

            // check headers
            Debug.Assert(request.Context is not null);
            var header = request.Context.Session.HttpClient.Request.Headers.FirstOrDefault(
                h =>
                    (!string.IsNullOrEmpty(apiVersion.Name) && h.Value.Contains(apiVersion.Name)) ||
                    (!string.IsNullOrEmpty(apiVersion.Properties?.Title) && h.Value.Contains(apiVersion.Properties.Title))
            );
            if (header is not null)
            {
                logger.LogDebug("Version {version} found in header {header}", $"{apiVersion.Name}/{apiVersion.Properties?.Title}", header.Name);
                return apiVersion;
            }
        }

        logger.LogDebug("No matching version found for {request}", requestUrl);
        return null;
    }

    internal static IEnumerable<string> GetUrls(this Api api)
    {
        if (api.Versions is null ||
            api.Versions.Length == 0)
        {
            return [];
        }

        var urlsFromDeployments = api.Deployments?.SelectMany(d =>
            d.Properties?.Server?.RuntimeUri ?? []) ?? [];
        var urlsFromVersions = api.Versions?.SelectMany(v =>
            v.Definitions?.SelectMany(d =>
                d.Definition?.Servers.Select(s => s.Url) ?? []) ?? []) ?? [];

        return new HashSet<string>([.. urlsFromDeployments, .. urlsFromVersions]);
    }

    internal static async Task LoadOpenApiDefinitionAsync(this ApiDefinition apiDefinition, ApiCenterClient apiCenterClient, ILogger logger)
    {
        if (apiDefinition.Definition is not null)
        {
            logger.LogDebug("API definition already loaded for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        Debug.Assert(apiDefinition.Id is not null);
        logger.LogDebug("Loading API definition for {apiDefinitionId}...", apiDefinition.Id);

        var definition = await apiCenterClient.GetDefinitionAsync(apiDefinition.Id);
        if (definition is null)
        {
            logger.LogError("Failed to deserialize API definition for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        apiDefinition.Properties = definition.Properties;
        if (apiDefinition.Properties?.Specification?.Name != "openapi")
        {
            logger.LogDebug("API definition is not OpenAPI for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        var exportResult = await apiCenterClient.PostExportSpecificationAsync(apiDefinition.Id);
        if (exportResult is null)
        {
            logger.LogError("Failed to deserialize exported API definition for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        if (exportResult.Format != ApiSpecExportResultFormat.Inline)
        {
            logger.LogDebug("API definition is not inline for {apiDefinitionId}", apiDefinition.Id);
            return;
        }

        try
        {
            apiDefinition.Definition = new OpenApiStringReader().Read(exportResult.Value, out _);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse OpenAPI document for {apiDefinitionId}", apiDefinition.Id);
        }
    }

    internal static async Task<Dictionary<string, ApiDefinition>> GetApiDefinitionsByUrlAsync(this Api[] apis, ApiCenterClient apiCenterClient, ILogger logger)
    {
        logger.LogInformation("Loading API definitions from API Center...");

        // key is the runtime URI, value is the API definition
        var apiDefinitions = new Dictionary<string, ApiDefinition>();

        foreach (var api in apis)
        {
            Debug.Assert(api.Id is not null);

            logger.LogDebug("Loading API definitions for {apiName}...", api.Id);

            // load definitions from deployments
            await api.LoadDeploymentsAsync(apiCenterClient);
            // LoadDeployments sets api.Deployments to an empty array if no deployments are found
            foreach (var deployment in api.Deployments!)
            {
                Debug.Assert(deployment.Properties?.Server is not null);
                Debug.Assert(deployment.Properties?.DefinitionId is not null);

                if (deployment.Properties.Server.RuntimeUri.Length == 0)
                {
                    logger.LogDebug("No runtime URIs found for deployment {deploymentName}", deployment.Name);
                    continue;
                }

                foreach (var runtimeUri in deployment.Properties.Server.RuntimeUri)
                {
                    apiDefinitions[runtimeUri] = new ApiDefinition
                    {
                        Id = deployment.Properties.DefinitionId
                    };
                }
            }

            // load definitions from versions
            await api.LoadVersionsAsync(apiCenterClient);
            // LoadVersions sets api.Versions to an empty array if no versions are found
            foreach (var version in api.Versions!)
            {
                Debug.Assert(version.Id is not null);

                await version.LoadDefinitionsAsync(apiCenterClient);
                // LoadDefinitions sets version.Definitions to an empty array if no definitions are found
                foreach (var definition in version.Definitions!)
                {
                    Debug.Assert(definition.Id is not null);

                    await definition.LoadOpenApiDefinitionAsync(apiCenterClient, logger);

                    if (definition.Definition is null)
                    {
                        logger.LogDebug("No OpenAPI definition found for {definitionId}", definition.Id);
                        continue;
                    }

                    if (!definition.Definition.Servers.Any())
                    {
                        logger.LogDebug("No servers found for API definition {definitionId}", definition.Id);
                        continue;
                    }

                    foreach (var server in definition.Definition.Servers)
                    {
                        apiDefinitions[server.Url] = definition;
                    }
                }
            }
        }

        logger.LogDebug(
            "Loaded API definitions from API Center for APIs:{newLine}- {apis}",
            Environment.NewLine,
            string.Join($"{Environment.NewLine}- ", apiDefinitions.Keys)
        );

        return apiDefinitions;
    }

    internal static Api? FindApiByUrl(this Api[] apis, string requestUrl, ILogger logger)
    {
        var apiByUrl = apis
            .SelectMany(a => a.GetUrls().Select(u => (Api: a, Url: u)))
            .OrderByDescending(a => a.Url.Length);

        // find the longest matching URL
        var api = apiByUrl.FirstOrDefault(a => requestUrl.StartsWith(a.Url));
        if (api.Url == default)
        {
            logger.LogDebug("No matching API found for {request}", requestUrl);
            return null;
        }
        else
        {
            logger.LogDebug("{request} matches API {api}", requestUrl, api.Api.Name);
            return api.Api;
        }
    }

    internal static Api? FindApiByDefinition(this Api[] apis, ApiDefinition apiDefinition, ILogger logger)
    {
        var api = apis
            .FirstOrDefault(a =>
                (a.Versions?.Any(v => v.Definitions?.Any(d => d.Id == apiDefinition.Id) == true) == true) ||
                (a.Deployments?.Any(d => d.Properties?.DefinitionId == apiDefinition.Id) == true));
        if (api is null)
        {
            logger.LogDebug("No matching API found for {apiDefinitionId}", apiDefinition.Id);
            return null;
        }
        else
        {
            logger.LogDebug("API {api} found for {apiDefinitionId}", api.Name, apiDefinition.Id);
            return api;
        }
    }
}