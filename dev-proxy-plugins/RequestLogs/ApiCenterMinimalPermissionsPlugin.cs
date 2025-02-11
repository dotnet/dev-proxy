// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.ApiCenter;
using Microsoft.DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class ApiCenterMinimalPermissionsPluginReportApiResult
{
    public required string ApiId { get; init; }
    public required string ApiName { get; init; }
    public required string ApiDefinitionId { get; init; }
    public required string[] Requests { get; init; }
    public required string[] TokenPermissions { get; init; }
    public required string[] MinimalPermissions { get; init; }
    public required string[] ExcessivePermissions { get; init; }
    public required bool UsesMinimalPermissions { get; init; }
}

public class ApiCenterMinimalPermissionsPluginReport
{
    public required ApiCenterMinimalPermissionsPluginReportApiResult[] Results { get; init; }
    public required string[] UnmatchedRequests { get; init; }
    public required ApiPermissionError[] Errors { get; init; }
}

internal class ApiCenterMinimalPermissionsPluginConfiguration
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
}

public class ApiCenterMinimalPermissionsPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private readonly ApiCenterProductionVersionPluginConfiguration _configuration = new();
    private ApiCenterClient? _apiCenterClient;
    private Api[]? _apis;
    private Dictionary<string, ApiDefinition>? _apiDefinitionsByUrl;

    public override string Name => nameof(ApiCenterMinimalPermissionsPlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        try
        {
            _apiCenterClient = new(
                new()
                {
                    SubscriptionId = _configuration.SubscriptionId,
                    ResourceGroupName = _configuration.ResourceGroupName,
                    ServiceName = _configuration.ServiceName,
                    WorkspaceName = _configuration.WorkspaceName
                },
                Logger
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create API Center client. The {plugin} will not be used.", Name);
            return;
        }

        Logger.LogInformation("Plugin {plugin} connecting to Azure...", Name);
        try
        {
            _ = await _apiCenterClient.GetAccessTokenAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to authenticate with Azure. The {plugin} will not be used.", Name);
            return;
        }
        Logger.LogDebug("Plugin {plugin} auth confirmed...", Name);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object sender, RecordingArgs e)
    {
        var interceptedRequests = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedRequest &&
                !l.Message.StartsWith("OPTIONS") &&
                l.Context?.Session is not null &&
                l.Context.Session.HttpClient.Request.Headers.Any(h => h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            );
        if (!interceptedRequests.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Checking if recorded API requests use minimal permissions as defined in API Center...");

        Debug.Assert(_apiCenterClient is not null);

        _apis ??= await _apiCenterClient.GetApisAsync();
        if (_apis is null || _apis.Length == 0)
        {
            Logger.LogInformation("No APIs found in API Center");
            return;
        }

        // get all API definitions by URL so that we can easily match
        // API requests to API definitions, for permissions lookup
        _apiDefinitionsByUrl ??= await _apis.GetApiDefinitionsByUrlAsync(_apiCenterClient, Logger);

        var (requestsByApiDefinition, unmatchedApicRequests) = GetRequestsByApiDefinition(interceptedRequests, _apiDefinitionsByUrl);

        var errors = new List<ApiPermissionError>();
        var results = new List<ApiCenterMinimalPermissionsPluginReportApiResult>();
        var unmatchedRequests = new List<string>(
            unmatchedApicRequests.Select(r => r.Message)
        );
        
        foreach (var (apiDefinition, requests) in requestsByApiDefinition)
        {
            var minimalPermissions = CheckMinimalPermissions(requests, apiDefinition);

            var api = _apis.FindApiByDefinition(apiDefinition, Logger);
            var result = new ApiCenterMinimalPermissionsPluginReportApiResult
            {
                ApiId = api?.Id ?? "unknown",
                ApiName = api?.Properties?.Title ?? "unknown",
                ApiDefinitionId = apiDefinition.Id!,
                Requests = minimalPermissions.OperationsFromRequests
                    .Select(o => $"{o.Method} {o.OriginalUrl}")
                    .Distinct()
                    .ToArray(),
                TokenPermissions = minimalPermissions.TokenPermissions.Distinct().ToArray(),
                MinimalPermissions = minimalPermissions.MinimalScopes,
                ExcessivePermissions = minimalPermissions.TokenPermissions.Except(minimalPermissions.MinimalScopes).ToArray(),
                UsesMinimalPermissions = !minimalPermissions.TokenPermissions.Except(minimalPermissions.MinimalScopes).Any()
            };
            results.Add(result);

            var unmatchedApiRequests = minimalPermissions.OperationsFromRequests
                .Where(o => minimalPermissions.UnmatchedOperations.Contains($"{o.Method} {o.TokenizedUrl}"))
                .Select(o => $"{o.Method} {o.OriginalUrl}");
            unmatchedRequests.AddRange(unmatchedApiRequests);
            errors.AddRange(minimalPermissions.Errors);

            if (result.UsesMinimalPermissions)
            {
                Logger.LogInformation(
                    "API {apiName} is called with minimal permissions: {minimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.MinimalPermissions)
                );
            }
            else
            {
                Logger.LogWarning(
                    "Calling API {apiName} with excessive permissions: {excessivePermissions}. Minimal permissions are: {minimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.ExcessivePermissions),
                    string.Join(", ", result.MinimalPermissions)
                );
            }

            if (unmatchedApiRequests.Any())
            {
                Logger.LogWarning(
                    "Unmatched requests for API {apiName}:{newLine}- {unmatchedRequests}",
                    result.ApiName,
                    Environment.NewLine,
                    string.Join($"{Environment.NewLine}- ", unmatchedApiRequests)
                );
            }

            if (minimalPermissions.Errors.Count != 0)
            {
                Logger.LogWarning(
                    "Errors for API {apiName}:{newLine}- {errors}",
                    result.ApiName,
                    Environment.NewLine,
                    string.Join($"{Environment.NewLine}- ", minimalPermissions.Errors.Select(e => $"{e.Request}: {e.Error}"))
                );
            }
        }

        var report = new ApiCenterMinimalPermissionsPluginReport()
        {
            Results = [.. results],
            UnmatchedRequests = [.. unmatchedRequests],
            Errors = [.. errors]
        };

        StoreReport(report, e);
    }

    private ApiPermissionsInfo CheckMinimalPermissions(IEnumerable<RequestLog> requests, ApiDefinition apiDefinition)
    {
        Logger.LogInformation("Checking minimal permissions for API {apiName}...", apiDefinition.Definition!.Servers.First().Url);

        return apiDefinition.Definition.CheckMinimalPermissions(requests, Logger);
    }

    private (Dictionary<ApiDefinition, List<RequestLog>> RequestsByApiDefinition, IEnumerable<RequestLog> UnmatchedRequests) GetRequestsByApiDefinition(IEnumerable<RequestLog> interceptedRequests, Dictionary<string, ApiDefinition> apiDefinitionsByUrl)
    {
        var unmatchedRequests = new List<RequestLog>();
        var requestsByApiDefinition = new Dictionary<ApiDefinition, List<RequestLog>>();
        foreach (var request in interceptedRequests)
        {
            var url = request.Message.Split(' ')[1];
            Logger.LogDebug("Matching request {requestUrl} to API definitions...", url);

            var matchingKey = apiDefinitionsByUrl.Keys.FirstOrDefault(url.StartsWith);
            if (matchingKey is null)
            {
                Logger.LogDebug("No matching API definition found for {requestUrl}", url);
                unmatchedRequests.Add(request);
                continue;
            }

            if (!requestsByApiDefinition.TryGetValue(apiDefinitionsByUrl[matchingKey], out List<RequestLog>? value))
            {
                value = [];
                requestsByApiDefinition[apiDefinitionsByUrl[matchingKey]] = value;
            }

            value.Add(request);
        }

        return (requestsByApiDefinition, unmatchedRequests);
    }
}