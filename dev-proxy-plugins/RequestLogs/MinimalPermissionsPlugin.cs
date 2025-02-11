// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class MinimalPermissionsPluginReportApiResult
{
    public required string ApiName { get; init; }
    public required string[] Requests { get; init; }
    public required string[] TokenPermissions { get; init; }
    public required string[] MinimalPermissions { get; init; }
    public required string[] ExcessivePermissions { get; init; }
    public required bool UsesMinimalPermissions { get; init; }
}

public class MinimalPermissionsPluginReport
{
    public required MinimalPermissionsPluginReportApiResult[] Results { get; init; }
    public required string[] UnmatchedRequests { get; init; }
    public required ApiPermissionError[] Errors { get; init; }
}

public class MinimalPermissionsPluginConfiguration
{
    public string? ApiSpecsFolderPath { get; set; }
}

public class MinimalPermissionsPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private readonly MinimalPermissionsPluginConfiguration _configuration = new();
    private Dictionary<string, OpenApiDocument>? _apiSpecsByUrl;
    public override string Name => nameof(MinimalPermissionsPlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        if (string.IsNullOrWhiteSpace(_configuration.ApiSpecsFolderPath))
        {
            throw new InvalidOperationException("ApiSpecsFolderPath is required.");
        }
        if (!Path.Exists(_configuration.ApiSpecsFolderPath))
        {
            throw new InvalidOperationException($"ApiSpecsFolderPath '{_configuration.ApiSpecsFolderPath}' does not exist.");
        }

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

#pragma warning disable CS1998
    private async Task AfterRecordingStopAsync(object sender, RecordingArgs e)
#pragma warning restore CS1998
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

        Logger.LogInformation("Checking if recorded API requests use minimal permissions as defined in API specs...");

        _apiSpecsByUrl ??= LoadApiSpecs(_configuration.ApiSpecsFolderPath!);
        if (_apiSpecsByUrl is null || _apiSpecsByUrl.Count == 0)
        {
            Logger.LogWarning("No API definitions found in the specified folder.");
            return;
        }

        var (requestsByApiSpec, unmatchedApiSpecRequests) = GetRequestsByApiSpec(interceptedRequests, _apiSpecsByUrl);

        var errors = new List<ApiPermissionError>();
        var results = new List<MinimalPermissionsPluginReportApiResult>();
        var unmatchedRequests = new List<string>(
            unmatchedApiSpecRequests.Select(r => r.Message)
        );

        foreach (var (apiSpec, requests) in requestsByApiSpec)
        {
            var minimalPermissions = apiSpec.CheckMinimalPermissions(requests, Logger);

            var result = new MinimalPermissionsPluginReportApiResult
            {
                ApiName = GetApiName(minimalPermissions.OperationsFromRequests.First().OriginalUrl),
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

        var report = new MinimalPermissionsPluginReport()
        {
            Results = [.. results],
            UnmatchedRequests = [.. unmatchedRequests],
            Errors = [.. errors]
        };

        StoreReport(report, e);
    }

    private Dictionary<string, OpenApiDocument> LoadApiSpecs(string apiSpecsFolderPath)
    {
        var apiDefinitions = new Dictionary<string, OpenApiDocument>();
        foreach (var file in Directory.EnumerateFiles(apiSpecsFolderPath, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping file '{file}' because it is not a JSON or YAML file", file);
                continue;
            }

            Logger.LogDebug("Processing file '{file}'...", file);
            try
            {
                var apiDefinition = new OpenApiStringReader().Read(File.ReadAllText(file), out _);
                if (apiDefinition is null)
                {
                    continue;
                }
                if (apiDefinition.Servers is null || apiDefinition.Servers.Count == 0)
                {
                    Logger.LogDebug("No servers found in API definition file '{file}'", file);
                    continue;
                }
                foreach (var server in apiDefinition.Servers)
                {
                    if (server.Url is null)
                    {
                        Logger.LogDebug("No URL found for server '{server}'", server.Description ?? "unnamed");
                        continue;
                    }
                    apiDefinitions[server.Url] = apiDefinition;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load API definition from file '{file}'", file);
            }
        }
        return apiDefinitions;
    }

    private (Dictionary<OpenApiDocument, List<RequestLog>> RequestsByApiSpec, IEnumerable<RequestLog> UnmatchedRequests) GetRequestsByApiSpec(IEnumerable<RequestLog> interceptedRequests, Dictionary<string, OpenApiDocument> apiSpecsByUrl)
    {
        var unmatchedRequests = new List<RequestLog>();
        var requestsByApiSpec = new Dictionary<OpenApiDocument, List<RequestLog>>();
        foreach (var request in interceptedRequests)
        {
            var url = request.Message.Split(' ')[1];
            Logger.LogDebug("Matching request {requestUrl} to API specs...", url);

            var matchingKey = apiSpecsByUrl.Keys.FirstOrDefault(url.StartsWith);
            if (matchingKey is null)
            {
                Logger.LogDebug("No matching API spec found for {requestUrl}", url);
                unmatchedRequests.Add(request);
                continue;
            }

            if (!requestsByApiSpec.TryGetValue(apiSpecsByUrl[matchingKey], out List<RequestLog>? value))
            {
                value = [];
                requestsByApiSpec[apiSpecsByUrl[matchingKey]] = value;
            }

            value.Add(request);
        }

        return (requestsByApiSpec, unmatchedRequests);
    }

    private static string GetApiName(string url)
    {
        var uri = new Uri(url);
        return uri.Authority;
    }
}
