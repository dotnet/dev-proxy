// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.ApiCenter;
using DevProxy.Plugins.Models;
using DevProxy.Plugins.Models.ApiCenter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Diagnostics;

namespace DevProxy.Plugins.Reporting;

public sealed class ApiCenterMinimalPermissionsPluginConfiguration
{
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
}

public sealed class ApiCenterMinimalPermissionsPlugin(
    HttpClient httpClient,
    ILogger<ApiCenterMinimalPermissionsPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    BaseReportingPlugin<ApiCenterMinimalPermissionsPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection,
        proxyStorage)
{
    private ApiCenterClient? _apiCenterClient;
    private Api[]? _apis;
    private Dictionary<string, ApiDefinition>? _apiDefinitionsByUrl;

    public override string Name => nameof(ApiCenterMinimalPermissionsPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        try
        {
            _apiCenterClient = ActivatorUtilities.CreateInstance<ApiCenterClient>(e.ServiceProvider,
                new ApiCenterClientConfiguration
                {
                    SubscriptionId = Configuration.SubscriptionId,
                    ResourceGroupName = Configuration.ResourceGroupName,
                    ServiceName = Configuration.ServiceName,
                    WorkspaceName = Configuration.WorkspaceName
                }
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create API Center client. The {Plugin} will not be used.", Name);
            Enabled = false;
            return;
        }

        Logger.LogInformation("Plugin {Plugin} connecting to Azure...", Name);
        try
        {
            _ = await _apiCenterClient.GetAccessTokenAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to authenticate with Azure. The {Plugin} will not be used.", Name);
            Enabled = false;
            return;
        }
        Logger.LogDebug("Plugin {Plugin} auth confirmed...", Name);
    }

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        var interceptedRequests = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedRequest &&
                !l.Message.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                l.Request is not null &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Request.RequestUri?.AbsoluteUri ?? string.Empty) &&
                l.Request.Headers.Authorization is not null
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
        _apiDefinitionsByUrl ??= await _apis.GetApiDefinitionsByUrlAsync(_apiCenterClient, Logger, cancellationToken);

        var (requestsByApiDefinition, unmatchedApicRequests) = GetRequestsByApiDefinition(interceptedRequests, _apiDefinitionsByUrl);

        var errors = new List<ApiPermissionError>();
        var results = new List<ApiCenterMinimalPermissionsPluginReportApiResult>();
        var unmatchedRequests = new List<string>(
            unmatchedApicRequests.Select(r => r.Message)
        );

        foreach (var (apiDefinition, requests) in requestsByApiDefinition)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var minimalPermissions = CheckMinimalPermissions(requests, apiDefinition);

            var api = _apis.FindApiByDefinition(apiDefinition, Logger);
            var result = new ApiCenterMinimalPermissionsPluginReportApiResult
            {
                ApiId = api?.Id ?? "unknown",
                ApiName = api?.Properties?.Title ?? "unknown",
                ApiDefinitionId = apiDefinition.Id!,
                Requests = [.. minimalPermissions.OperationsFromRequests
                    .Select(o => $"{o.Method} {o.OriginalUrl}")
                    .Distinct()],
                TokenPermissions = [.. minimalPermissions.TokenPermissions.Distinct()],
                MinimalPermissions = minimalPermissions.MinimalScopes,
                ExcessivePermissions = [.. minimalPermissions.TokenPermissions.Except(minimalPermissions.MinimalScopes)],
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
                    "API {ApiName} is called with minimal permissions: {MinimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.MinimalPermissions)
                );
            }
            else
            {
                Logger.LogWarning(
                    "Calling API {ApiName} with excessive permissions: {ExcessivePermissions}. Minimal permissions are: {MinimalPermissions}",
                    result.ApiName,
                    string.Join(", ", result.ExcessivePermissions),
                    string.Join(", ", result.MinimalPermissions)
                );
            }

            if (unmatchedApiRequests.Any())
            {
                Logger.LogWarning(
                    "Unmatched requests for API {ApiName}:{NewLine}- {UnmatchedRequests}",
                    result.ApiName,
                    Environment.NewLine,
                    string.Join($"{Environment.NewLine}- ", unmatchedApiRequests)
                );
            }

            if (minimalPermissions.Errors.Any())
            {
                Logger.LogWarning(
                    "Errors for API {ApiName}:{NewLine}- {Errors}",
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

        StoreReport(report);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    private ApiPermissionsInfo CheckMinimalPermissions(IEnumerable<RequestLog> requests, ApiDefinition apiDefinition)
    {
        Logger.LogInformation("Checking minimal permissions for API {ApiName}...", apiDefinition.Definition!.Servers.First().Url);

        return apiDefinition.Definition.CheckMinimalPermissions(requests, Logger);
    }

    private (Dictionary<ApiDefinition, List<RequestLog>> RequestsByApiDefinition, IEnumerable<RequestLog> UnmatchedRequests) GetRequestsByApiDefinition(IEnumerable<RequestLog> interceptedRequests, Dictionary<string, ApiDefinition> apiDefinitionsByUrl)
    {
        var unmatchedRequests = new List<RequestLog>();
        var requestsByApiDefinition = new Dictionary<ApiDefinition, List<RequestLog>>();
        foreach (var request in interceptedRequests)
        {
            var url = request.Message.Split(' ')[1];
            Logger.LogDebug("Matching request {RequestUrl} to API definitions...", url);

            var matchingKey = apiDefinitionsByUrl.Keys.FirstOrDefault(url.StartsWith);
            if (matchingKey is null)
            {
                Logger.LogDebug("No matching API definition found for {RequestUrl}", url);
                unmatchedRequests.Add(request);
                continue;
            }

            if (!requestsByApiDefinition.TryGetValue(apiDefinitionsByUrl[matchingKey], out var value))
            {
                value = [];
                requestsByApiDefinition[apiDefinitionsByUrl[matchingKey]] = value;
            }

            value.Add(request);
        }

        return (requestsByApiDefinition, unmatchedRequests);
    }
}