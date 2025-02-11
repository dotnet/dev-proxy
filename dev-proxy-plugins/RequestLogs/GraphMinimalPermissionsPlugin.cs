// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DevProxy.Plugins.MinimalPermissions;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class GraphMinimalPermissionsPluginReport
{
    public required IEnumerable<GraphRequestInfo> Requests { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GraphPermissionsType PermissionsType { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    public required IEnumerable<string> Errors { get; init; }
}

internal class GraphMinimalPermissionsPluginConfiguration
{
    public GraphPermissionsType Type { get; set; } = GraphPermissionsType.Delegated;
}

public class GraphMinimalPermissionsPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(GraphMinimalPermissionsPlugin);
    private readonly GraphMinimalPermissionsPluginConfiguration _configuration = new();

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        if (!e.RequestLogs.Any())
        {
            return;
        }

        var methodAndUrlComparer = new MethodAndUrlComparer();
        var endpoints = new List<(string method, string url)>();

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedRequest)
            {
                continue;
            }

            var methodAndUrlString = request.Message;
            var methodAndUrl = GetMethodAndUrl(methodAndUrlString);
            if (methodAndUrl.method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uri = new Uri(methodAndUrl.url);
            if (!ProxyUtils.IsGraphUrl(uri))
            {
                continue;
            }

            if (ProxyUtils.IsGraphBatchUrl(uri))
            {
                var graphVersion = ProxyUtils.IsGraphBetaUrl(uri) ? "beta" : "v1.0";
                var requestsFromBatch = GetRequestsFromBatch(request.Context?.Session.HttpClient.Request.BodyString!, graphVersion, uri.Host);
                endpoints.AddRange(requestsFromBatch);
            }
            else
            {
                methodAndUrl = (methodAndUrl.method, GetTokenizedUrl(methodAndUrl.url));
                endpoints.Add(methodAndUrl);
            }
        }

        // Remove duplicates
        endpoints = endpoints.Distinct(methodAndUrlComparer).ToList();

        if (endpoints.Count == 0)
        {
            Logger.LogInformation("No requests to Microsoft Graph endpoints recorded. Will not retrieve minimal permissions.");
            return;
        }

        Logger.LogInformation("Retrieving minimal permissions for:\r\n{endpoints}\r\n", string.Join(Environment.NewLine, endpoints.Select(e => $"- {e.method} {e.url}")));

        Logger.LogWarning("This plugin is in preview and may not return the correct results.\r\nPlease review the permissions and test your app before using them in production.\r\nIf you have any feedback, please open an issue at https://aka.ms/devproxy/issue.\r\n");

        var report = await DetermineMinimalScopesAsync(endpoints);
        if (report is not null)
        {
            StoreReport(report, e);
        }
    }

    private static (string method, string url)[] GetRequestsFromBatch(string batchBody, string graphVersion, string graphHostName)
    {
        var requests = new List<(string, string)>();

        if (string.IsNullOrEmpty(batchBody))
        {
            return [.. requests];
        }

        try
        {
            var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(batchBody, ProxyUtils.JsonSerializerOptions);
            if (batch == null)
            {
                return [.. requests];
            }

            foreach (var request in batch.Requests)
            {
                try
                {
                    var method = request.Method;
                    var url = request.Url;
                    var absoluteUrl = $"https://{graphHostName}/{graphVersion}{url}";
                    requests.Add((method, GetTokenizedUrl(absoluteUrl)));
                }
                catch { }
            }
        }
        catch { }

        return [.. requests];
    }

    private async Task<GraphMinimalPermissionsPluginReport?> DetermineMinimalScopesAsync(IEnumerable<(string method, string url)> endpoints)
    {
        var payload = endpoints.Select(e => new GraphRequestInfo { Method = e.method, Url = e.url });

        try
        {
            var url = $"https://graphexplorerapi.azurewebsites.net/permissions?scopeType={GraphUtils.GetScopeTypeString(_configuration.Type)}";
            using var client = new HttpClient();
            var stringPayload = JsonSerializer.Serialize(payload, ProxyUtils.JsonSerializerOptions);
            Logger.LogDebug("Calling {url} with payload\r\n{stringPayload}", url, stringPayload);

            var response = await client.PostAsJsonAsync(url, payload);
            var content = await response.Content.ReadAsStringAsync();

            Logger.LogDebug("Response:\r\n{content}", content);

            var resultsAndErrors = JsonSerializer.Deserialize<GraphResultsAndErrors>(content, ProxyUtils.JsonSerializerOptions);
            var minimalScopes = resultsAndErrors?.Results?.Select(p => p.Value) ?? [];
            var errors = resultsAndErrors?.Errors?.Select(e => $"- {e.Url} ({e.Message})") ?? [];

            if (_configuration.Type == GraphPermissionsType.Delegated)
            {
                minimalScopes = await GraphUtils.UpdateUserScopesAsync(minimalScopes, endpoints, _configuration.Type, Logger);
            }

            if (minimalScopes.Any())
            {
                Logger.LogInformation("Minimal permissions:\r\n{permissions}", string.Join(", ", minimalScopes));
            }
            if (errors.Any())
            {
                Logger.LogError("Couldn't determine minimal permissions for the following URLs:\r\n{errors}", string.Join(Environment.NewLine, errors));
            }

            return new GraphMinimalPermissionsPluginReport
            {
                Requests = payload.ToArray(),
                PermissionsType = _configuration.Type,
                MinimalPermissions = minimalScopes,
                Errors = errors.ToArray()
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while retrieving minimal permissions:");
            return null;
        }
    }

    private static (string method, string url) GetMethodAndUrl(string message)
    {
        var info = message.Split(" ");
        if (info.Length > 2)
        {
            info = [info[0], string.Join(" ", info.Skip(1))];
        }
        return (info[0], info[1]);
    }

    private static string GetTokenizedUrl(string absoluteUrl)
    {
        var sanitizedUrl = ProxyUtils.SanitizeUrl(absoluteUrl);
        return "/" + string.Join("", new Uri(sanitizedUrl).Segments.Skip(2).Select(Uri.UnescapeDataString));
    }
}
