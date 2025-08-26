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
using Microsoft.OpenApi.Models;
using System.Diagnostics;

namespace DevProxy.Plugins.Generation;

public sealed class ApiCenterOnboardingPluginConfiguration
{
    public bool CreateApicEntryForNewApis { get; set; } = true;
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
}

public sealed class ApiCenterOnboardingPlugin(
    HttpClient httpClient,
    ILogger<ApiCenterOnboardingPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    BaseReportingPlugin<ApiCenterOnboardingPluginConfiguration>(
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

    public override string Name => nameof(ApiCenterOnboardingPlugin);

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
            _ = await _apiCenterClient.GetAccessTokenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to authenticate with Azure. The {Plugin} will not be used.", Name);
            Enabled = false;
            return;
        }
        Logger.LogDebug("Plugin {Plugin} auth confirmed...", Name);
    }

    public override Func<RecordingArgs, CancellationToken, Task>? HandleRecordingStopAsync => AfterRecordingStopAsync;

    public async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Checking if recorded API requests belong to APIs in API Center...");

        Debug.Assert(_apiCenterClient is not null);

        _apis ??= await _apiCenterClient.GetApisAsync();

        if (_apis == null || _apis.Length == 0)
        {
            Logger.LogInformation("No APIs found in API Center");
            return;
        }

        _apiDefinitionsByUrl ??= await _apis.GetApiDefinitionsByUrlAsync(_apiCenterClient, Logger, cancellationToken);

        var newApis = new List<(string method, string url)>();
        var interceptedRequests = e.RequestLogs
            .Where(l => l.MessageType == MessageType.InterceptedRequest)
            .Select(request =>
            {
                var methodAndUrl = request.Message.Split(' ');
                return (method: methodAndUrl[0], url: methodAndUrl[1]);
            })
            .Where(r => !r.method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, r.url))
            .Distinct();

        var existingApis = new List<ApiCenterOnboardingPluginReportExistingApiInfo>();

        foreach (var request in interceptedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (method, url) = request;

            Logger.LogDebug("Processing request {Method} {Url}...", method, url);

            var apiDefinition = _apiDefinitionsByUrl.FirstOrDefault(x =>
                url.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (apiDefinition is null ||
                apiDefinition.Id is null)
            {
                Logger.LogDebug("No matching API definition not found for {Url}. Adding new API...", url);
                newApis.Add((method, url));
                continue;
            }

            await apiDefinition.LoadOpenApiDefinitionAsync(_apiCenterClient, Logger, cancellationToken);

            if (apiDefinition.Definition is null)
            {
                Logger.LogDebug("API definition not found for {Url} so nothing to compare to. Adding new API...", url);
                newApis.Add(new(method, url));
                continue;
            }

            var pathItem = apiDefinition.Definition.FindMatchingPathItem(url, Logger);
            if (pathItem is null)
            {
                Logger.LogDebug("No matching path found for {Url}. Adding new API...", url);
                newApis.Add(new(method, url));
                continue;
            }

            var operation = pathItem.Value.Value.Operations.FirstOrDefault(x =>
                x.Key.ToString().Equals(method, StringComparison.OrdinalIgnoreCase)).Value;
            if (operation is null)
            {
                Logger.LogDebug("No matching operation found for {Method} {Url}. Adding new API...", method, url);
                newApis.Add(new(method, url));
                continue;
            }

            existingApis.Add(new()
            {
                MethodAndUrl = $"{method} {url}",
                ApiDefinitionId = apiDefinition.Id,
                OperationId = operation.OperationId
            });
        }

        if (newApis.Count == 0)
        {
            Logger.LogInformation("No new APIs found");
            StoreReport(new ApiCenterOnboardingPluginReport
            {
                ExistingApis = [.. existingApis],
                NewApis = []
            });
            return;
        }

        // dedupe newApis
        newApis = [.. newApis.Distinct()];

        StoreReport(new ApiCenterOnboardingPluginReport
        {
            ExistingApis = [.. existingApis],
            NewApis = [.. newApis.Select(a => new ApiCenterOnboardingPluginReportNewApiInfo
            {
                Method = a.method,
                Url = a.url
            })]
        });

        var apisPerSchemeAndHost = newApis.GroupBy(x =>
        {
            var u = new Uri(x.url);
            return u.GetLeftPart(UriPartial.Authority);
        });

        var newApisMessageChunks = new List<string>(["New APIs that aren't registered in Azure API Center:", ""]);
        foreach (var apiPerHost in apisPerSchemeAndHost)
        {
            newApisMessageChunks.Add($"{apiPerHost.Key}:");
            newApisMessageChunks.AddRange(apiPerHost.Select(a => $"  {a.method} {a.url}"));
        }

        Logger.LogInformation("{NewApis}", string.Join(Environment.NewLine, newApisMessageChunks));

        if (!Configuration.CreateApicEntryForNewApis)
        {
            return;
        }

        var generatedOpenApiSpecs = ProxyStorage.GlobalData.TryGetValue(OpenApiSpecGeneratorPlugin.GeneratedOpenApiSpecsKey, out var specs) ? specs as Dictionary<string, string> : [];
        await CreateApisInApiCenterAsync(apisPerSchemeAndHost, generatedOpenApiSpecs!, cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    async Task CreateApisInApiCenterAsync(
        IEnumerable<IGrouping<string, (string method, string url)>> apisPerHost,
        Dictionary<string, string> generatedOpenApiSpecs,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Creating new API entries in API Center...");

        foreach (var apiPerHost in apisPerHost)
        {
            var schemeAndHost = apiPerHost.Key;

            var api = await CreateApiAsync(schemeAndHost, apiPerHost, cancellationToken);
            if (api is null)
            {
                continue;
            }

            Debug.Assert(api.Id is not null);

            if (!generatedOpenApiSpecs.TryGetValue(schemeAndHost, out var openApiSpecFilePath))
            {
                Logger.LogDebug("No OpenAPI spec found for {Host}", schemeAndHost);
                continue;
            }

            var apiVersion = await CreateApiVersionAsync(api.Id, cancellationToken);
            if (apiVersion is null)
            {
                continue;
            }

            Debug.Assert(apiVersion.Id is not null);

            var apiDefinition = await CreateApiDefinitionAsync(apiVersion.Id, cancellationToken);
            if (apiDefinition is null)
            {
                continue;
            }

            Debug.Assert(apiDefinition.Id is not null);

            await ImportApiDefinitionAsync(apiDefinition.Id, openApiSpecFilePath, cancellationToken);
        }
    }

    async Task<Api?> CreateApiAsync(string schemeAndHost, IEnumerable<(string method, string url)> apiRequests, CancellationToken cancellationToken)
    {
        Debug.Assert(_apiCenterClient is not null);

        // trim to 50 chars which is max length for API name
        var apiName = $"new-{schemeAndHost
            .Replace(".", "-", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}".MaxLength(50);
        Logger.LogInformation("  Creating API {ApiName} for {Host}...", apiName, schemeAndHost);

        var title = $"New APIs: {schemeAndHost}";
        var description = new List<string>(["New APIs discovered by Dev Proxy", ""]);
        description.AddRange([.. apiRequests.Select(a => $"  {a.method} {a.url}")]);
        var api = new Api
        {
            Properties = new()
            {
                Title = title,
                Description = string.Join(Environment.NewLine, description),
                Kind = ApiKind.REST
            }
        };

        var newApi = await _apiCenterClient.PutApiAsync(api, apiName, cancellationToken);
        if (newApi is not null)
        {
            Logger.LogDebug("API created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API {ApiName} for {Host}", apiName, schemeAndHost);
        }

        return newApi;
    }

    async Task<ApiVersion?> CreateApiVersionAsync(string apiId, CancellationToken cancellationToken)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Creating API version for {Api}...", apiId);

        var apiVersion = new ApiVersion
        {
            Properties = new()
            {
                Title = "v1.0",
                LifecycleStage = ApiLifecycleStage.Production
            }
        };

        var newApiVersion = await _apiCenterClient.PutVersionAsync(apiVersion, apiId, "v1-0", cancellationToken);
        if (newApiVersion is not null)
        {
            Logger.LogDebug("API version created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API version for {Api}", apiId[apiId.LastIndexOf('/')..]);
        }

        return newApiVersion;
    }

    async Task<ApiDefinition?> CreateApiDefinitionAsync(string apiVersionId, CancellationToken cancellationToken)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Creating API definition for {Api}...", apiVersionId);

        var apiDefinition = new ApiDefinition
        {
            Properties = new()
            {
                Title = "OpenAPI"
            }
        };
        var newApiDefinition = await _apiCenterClient.PutDefinitionAsync(apiDefinition, apiVersionId, "openapi", cancellationToken);
        if (newApiDefinition is not null)
        {
            Logger.LogDebug("API definition created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API definition for {ApiVersion}", apiVersionId);
        }

        return newApiDefinition;
    }

    async Task ImportApiDefinitionAsync(string apiDefinitionId, string openApiSpecFilePath, CancellationToken cancellationToken)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Importing API definition for {Api}...", apiDefinitionId);

        var openApiSpec = await File.ReadAllTextAsync(openApiSpecFilePath, cancellationToken);
        var apiSpecImport = new ApiSpecImport
        {
            Format = ApiSpecImportResultFormat.Inline,
            Value = openApiSpec,
            Specification = new()
            {
                Name = "openapi",
                Version = "3.0.1"
            }
        };
        var res = await _apiCenterClient.PostImportSpecificationAsync(apiSpecImport, apiDefinitionId, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            Logger.LogDebug("API definition imported successfully");
        }
        else
        {
            var resContent = res.ReasonPhrase;
            try
            {
                resContent = await res.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
            }

            Logger.LogError("Failed to import API definition for {ApiDefinition}. Status: {Status}, reason: {Reason}", apiDefinitionId, res.StatusCode, resContent);
        }
    }
}
