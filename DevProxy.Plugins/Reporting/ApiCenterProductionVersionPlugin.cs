// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.ApiCenter;
using DevProxy.Plugins.Models.ApiCenter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace DevProxy.Plugins.Reporting;

public sealed class ApiCenterProductionVersionPluginConfiguration
{
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
}

public sealed class ApiCenterProductionVersionPlugin(
    HttpClient httpClient,
    ILogger<ApiCenterProductionVersionPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<ApiCenterProductionVersionPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private ApiCenterClient? _apiCenterClient;
    private Api[]? _apis;

    public override string Name => nameof(ApiCenterProductionVersionPlugin);

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
            .Where(
                l => l.MessageType == MessageType.InterceptedRequest &&
                l.Context?.Session is not null &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri)
            );
        if (!interceptedRequests.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Checking if recorded API requests use production APIs as defined in API Center...");

        Debug.Assert(_apiCenterClient is not null);

        _apis ??= await _apiCenterClient.GetApisAsync();

        if (_apis == null || _apis.Length == 0)
        {
            Logger.LogInformation("No APIs found in API Center");
            return;
        }

        foreach (var api in _apis)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Debug.Assert(api.Id is not null);

            await api.LoadVersionsAsync(_apiCenterClient, cancellationToken);
            if (api.Versions == null || api.Versions.Length == 0)
            {
                Logger.LogInformation("No versions found for {Api}", api.Properties?.Title);
                continue;
            }

            foreach (var versionFromApiCenter in api.Versions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Debug.Assert(versionFromApiCenter.Id is not null);

                await versionFromApiCenter.LoadDefinitionsAsync(_apiCenterClient, cancellationToken);
                if (versionFromApiCenter.Definitions == null ||
                    versionFromApiCenter.Definitions.Length == 0)
                {
                    Logger.LogDebug("No definitions found for version {VersionId}", versionFromApiCenter.Id);
                    continue;
                }

                var definitions = new List<ApiDefinition>();
                foreach (var definitionFromApiCenter in versionFromApiCenter.Definitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Debug.Assert(definitionFromApiCenter.Id is not null);

                    await definitionFromApiCenter.LoadOpenApiDefinitionAsync(_apiCenterClient, Logger, cancellationToken);

                    if (definitionFromApiCenter.Definition is null)
                    {
                        Logger.LogDebug("API definition not found for {DefinitionId}", definitionFromApiCenter.Id);
                        continue;
                    }

                    if (!definitionFromApiCenter.Definition.Servers.Any())
                    {
                        Logger.LogDebug("No servers found for API definition {DefinitionId}", definitionFromApiCenter.Id);
                        continue;
                    }

                    definitions.Add(definitionFromApiCenter);
                }

                versionFromApiCenter.Definitions = [.. definitions];
            }
        }

        Logger.LogInformation("Analyzing recorded requests...");

        var report = new ApiCenterProductionVersionPluginReport();

        foreach (var request in interceptedRequests)
        {
            var methodAndUrlString = request.Message;
            var methodAndUrl = methodAndUrlString.Split(' ');
            var (method, url) = (methodAndUrl[0], methodAndUrl[1]);
            if (method == "OPTIONS")
            {
                continue;
            }

            var api = _apis.FindApiByUrl(url, Logger);
            if (api == null)
            {
                report.Add(new()
                {
                    Method = method,
                    Url = url,
                    Status = ApiCenterProductionVersionPluginReportItemStatus.NotRegistered
                });
                continue;
            }

            var version = api.GetVersion(request, url, Logger);
            if (version is null)
            {
                report.Add(new()
                {
                    Method = method,
                    Url = url,
                    Status = ApiCenterProductionVersionPluginReportItemStatus.NotRegistered
                });
                continue;
            }

            Debug.Assert(version.Properties is not null);
            var lifecycleStage = version.Properties.LifecycleStage;

            if (lifecycleStage != ApiLifecycleStage.Production)
            {
                Debug.Assert(api.Versions is not null);

                var productionVersions = api.Versions
                    .Where(v => v.Properties?.LifecycleStage == ApiLifecycleStage.Production)
                    .Select(v => v.Properties?.Title);

                var recommendation = productionVersions.Any() ?
                    string.Format(CultureInfo.InvariantCulture, "Request {0} uses API version {1} which is defined as {2}. Upgrade to a production version of the API. Recommended versions: {3}", methodAndUrlString, api.Versions.First(v => v.Properties?.LifecycleStage == lifecycleStage).Properties?.Title, lifecycleStage, string.Join(", ", productionVersions)) :
                    string.Format(CultureInfo.InvariantCulture, "Request {0} uses API version {1} which is defined as {2}.", methodAndUrlString, api.Versions.First(v => v.Properties?.LifecycleStage == lifecycleStage).Properties?.Title, lifecycleStage);

                Logger.LogWarning("{Recommendation}", recommendation);
                report.Add(new()
                {
                    Method = method,
                    Url = url,
                    Status = ApiCenterProductionVersionPluginReportItemStatus.NonProduction,
                    Recommendation = recommendation
                });
            }
            else
            {
                report.Add(new()
                {
                    Method = method,
                    Url = url,
                    Status = ApiCenterProductionVersionPluginReportItemStatus.Production
                });
            }
        }

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }
}