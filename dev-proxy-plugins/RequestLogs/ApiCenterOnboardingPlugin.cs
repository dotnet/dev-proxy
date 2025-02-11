// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.ApiCenter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class ApiCenterOnboardingPluginReportExistingApiInfo
{
    public required string MethodAndUrl { get; init; }
    public required string ApiDefinitionId { get; init; }
    public required string OperationId { get; init; }
}

public class ApiCenterOnboardingPluginReportNewApiInfo
{
    public required string Method { get; init; }
    public required string Url { get; init; }
}

public class ApiCenterOnboardingPluginReport
{
    public required ApiCenterOnboardingPluginReportExistingApiInfo[] ExistingApis { get; init; }
    public required ApiCenterOnboardingPluginReportNewApiInfo[] NewApis { get; init; }
}

internal class ApiCenterOnboardingPluginConfiguration
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroupName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string WorkspaceName { get; set; } = "default";
    public bool CreateApicEntryForNewApis { get; set; } = true;
}

public class ApiCenterOnboardingPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private readonly ApiCenterOnboardingPluginConfiguration _configuration = new();
    private ApiCenterClient? _apiCenterClient;
    private Api[]? _apis;
    private Dictionary<string, ApiDefinition>? _apiDefinitionsByUrl;

    public override string Name => nameof(ApiCenterOnboardingPlugin);

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

        _apiDefinitionsByUrl ??= await _apis.GetApiDefinitionsByUrlAsync(_apiCenterClient, Logger);

        var newApis = new List<(string method, string url)>();
        var interceptedRequests = e.RequestLogs
            .Where(l => l.MessageType == MessageType.InterceptedRequest)
            .Select(request =>
            {
                var methodAndUrl = request.Message.Split(' ');
                return (method: methodAndUrl[0], url: methodAndUrl[1]);
            })
            .Where(r => !r.method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            .Distinct();

        var existingApis = new List<ApiCenterOnboardingPluginReportExistingApiInfo>();

        foreach (var request in interceptedRequests)
        {
            var (method, url) = request;

            Logger.LogDebug("Processing request {method} {url}...", method, url);

            var apiDefinition = _apiDefinitionsByUrl.FirstOrDefault(x =>
                url.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (apiDefinition is null ||
                apiDefinition.Id is null)
            {
                Logger.LogDebug("No matching API definition not found for {url}. Adding new API...", url);
                newApis.Add((method, url));
                continue;
            }

            await apiDefinition.LoadOpenApiDefinitionAsync(_apiCenterClient, Logger);

            if (apiDefinition.Definition is null)
            {
                Logger.LogDebug("API definition not found for {url} so nothing to compare to. Adding new API...", url);
                newApis.Add(new(method, url));
                continue;
            }

            var pathItem = apiDefinition.Definition.FindMatchingPathItem(url, Logger);
            if (pathItem is null)
            {
                Logger.LogDebug("No matching path found for {url}. Adding new API...", url);
                newApis.Add(new(method, url));
                continue;
            }

            var operation = pathItem.Value.Value.Operations.FirstOrDefault(x =>
                x.Key.ToString().Equals(method, StringComparison.OrdinalIgnoreCase)).Value;
            if (operation is null)
            {
                Logger.LogDebug("No matching operation found for {method} {url}. Adding new API...", method, url);
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
                ExistingApis = existingApis.ToArray(),
                NewApis = []
            }, e);
            return;
        }

        // dedupe newApis
        newApis = newApis.Distinct().ToList();

        StoreReport(new ApiCenterOnboardingPluginReport
        {
            ExistingApis = [.. existingApis],
            NewApis = newApis.Select(a => new ApiCenterOnboardingPluginReportNewApiInfo
            {
                Method = a.method,
                Url = a.url
            }).ToArray()
        }, e);

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

        Logger.LogInformation(string.Join(Environment.NewLine, newApisMessageChunks));

        if (!_configuration.CreateApicEntryForNewApis)
        {
            return;
        }

        var generatedOpenApiSpecs = e.GlobalData.TryGetValue(OpenApiSpecGeneratorPlugin.GeneratedOpenApiSpecsKey, out var specs) ? specs as Dictionary<string, string> : new();
        await CreateApisInApiCenterAsync(apisPerSchemeAndHost, generatedOpenApiSpecs!);
    }

    async Task CreateApisInApiCenterAsync(IEnumerable<IGrouping<string, (string method, string url)>> apisPerHost, Dictionary<string, string> generatedOpenApiSpecs)
    {
        Logger.LogInformation("Creating new API entries in API Center...");

        foreach (var apiPerHost in apisPerHost)
        {
            var schemeAndHost = apiPerHost.Key;

            var api = await CreateApiAsync(schemeAndHost, apiPerHost);
            if (api is null)
            {
                continue;
            }

            Debug.Assert(api.Id is not null);

            if (!generatedOpenApiSpecs.TryGetValue(schemeAndHost, out var openApiSpecFilePath))
            {
                Logger.LogDebug("No OpenAPI spec found for {host}", schemeAndHost);
                continue;
            }

            var apiVersion = await CreateApiVersionAsync(api.Id);
            if (apiVersion is null)
            {
                continue;
            }

            Debug.Assert(apiVersion.Id is not null);

            var apiDefinition = await CreateApiDefinitionAsync(apiVersion.Id);
            if (apiDefinition is null)
            {
                continue;
            }

            Debug.Assert(apiDefinition.Id is not null);

            await ImportApiDefinitionAsync(apiDefinition.Id, openApiSpecFilePath);
        }
    }

    async Task<Api?> CreateApiAsync(string schemeAndHost, IEnumerable<(string method, string url)> apiRequests)
    {
        Debug.Assert(_apiCenterClient is not null);

        // trim to 50 chars which is max length for API name
        var apiName = $"new-{schemeAndHost.Replace(".", "-").Replace("http://", "").Replace("https://", "")}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}".MaxLength(50);
        Logger.LogInformation("  Creating API {apiName} for {host}...", apiName, schemeAndHost);

        var title = $"New APIs: {schemeAndHost}";
        var description = new List<string>(["New APIs discovered by Dev Proxy", ""]);
        description.AddRange(apiRequests.Select(a => $"  {a.method} {a.url}").ToArray());
        var api = new Api
        {
            Properties = new()
            {
                Title = title,
                Description = string.Join(Environment.NewLine, description),
                Kind = ApiKind.REST
            }
        };

        var newApi = await _apiCenterClient.PutApiAsync(api, apiName);
        if (newApi is not null)
        {
            Logger.LogDebug("API created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API {apiName} for {host}", apiName, schemeAndHost);
        }

        return newApi;
    }

    async Task<ApiVersion?> CreateApiVersionAsync(string apiId)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Creating API version for {api}...", apiId);

        var apiVersion = new ApiVersion
        {
            Properties = new()
            {
                Title = "v1.0",
                LifecycleStage = ApiLifecycleStage.Production
            }
        };

        var newApiVersion = await _apiCenterClient.PutVersionAsync(apiVersion, apiId, "v1-0");
        if (newApiVersion is not null)
        {
            Logger.LogDebug("API version created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API version for {api}", apiId.Substring(apiId.LastIndexOf('/')));
        }

        return newApiVersion;
    }

    async Task<ApiDefinition?> CreateApiDefinitionAsync(string apiVersionId)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Creating API definition for {api}...", apiVersionId);

        var apiDefinition = new ApiDefinition
        {
            Properties = new()
            {
                Title = "OpenAPI"
            }
        };
        var newApiDefinition = await _apiCenterClient.PutDefinitionAsync(apiDefinition, apiVersionId, "openapi");
        if (newApiDefinition is not null)
        {
            Logger.LogDebug("API definition created successfully");
        }
        else
        {
            Logger.LogError("Failed to create API definition for {apiVersion}", apiVersionId);
        }

        return newApiDefinition;
    }

    async Task ImportApiDefinitionAsync(string apiDefinitionId, string openApiSpecFilePath)
    {
        Debug.Assert(_apiCenterClient is not null);

        Logger.LogDebug("  Importing API definition for {api}...", apiDefinitionId);

        var openApiSpec = File.ReadAllText(openApiSpecFilePath);
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
        var res = await _apiCenterClient.PostImportSpecificationAsync(apiSpecImport, apiDefinitionId);
        if (res.IsSuccessStatusCode)
        {
            Logger.LogDebug("API definition imported successfully");
        }
        else
        {
            var resContent = res.ReasonPhrase;
            try
            {
                resContent = await res.Content.ReadAsStringAsync();
            }
            catch
            {
            }
            
            Logger.LogError("Failed to import API definition for {apiDefinition}. Status: {status}, reason: {reason}", apiDefinitionId, res.StatusCode, resContent);
        }
    }
}
