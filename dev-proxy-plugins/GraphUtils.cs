// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Json;
using Microsoft.DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins;

public class GraphUtils
{
    // throttle requests per workload
    public static string BuildThrottleKey(Request r) => BuildThrottleKey(r.RequestUri);

    public static string BuildThrottleKey(Uri uri)
    {
        if (uri.Segments.Length < 3)
        {
            return uri.Host;
        }

        // first segment is /
        // second segment is Graph version (v1.0, beta)
        // third segment is the workload (users, groups, etc.)
        // segment can end with / if there are other segments following
        var workload = uri.Segments[2].Trim('/');

        // TODO: handle 'me' which is a proxy to other resources

        return workload;
    }

    internal static string GetScopeTypeString(GraphPermissionsType type)
    {
        return type switch
        {
            GraphPermissionsType.Application => "Application",
            GraphPermissionsType.Delegated => "DelegatedWork",
            _ => throw new InvalidOperationException($"Unknown scope type: {type}")
        };
    }

    internal static async Task<IEnumerable<string>> UpdateUserScopesAsync(IEnumerable<string> minimalScopes, IEnumerable<(string method, string url)> endpoints, GraphPermissionsType permissionsType, ILogger logger)
    {
        var userEndpoints = endpoints.Where(e => e.url.Contains("/users/{", StringComparison.OrdinalIgnoreCase));
        if (!userEndpoints.Any())
        {
            return minimalScopes;
        }

        var newMinimalScopes = new HashSet<string>(minimalScopes);

        var url = $"https://graphexplorerapi.azurewebsites.net/permissions?scopeType={GetScopeTypeString(permissionsType)}";
        using var httpClient = new HttpClient();
        var urls = userEndpoints.Select(e => {
            logger.LogDebug("Getting permissions for {method} {url}", e.method, e.url);
            return $"{url}&requesturl={e.url}&method={e.method}";
        });
        var tasks = urls.Select(u => {
            logger.LogTrace("Calling {url}...", u);
            return httpClient.GetFromJsonAsync<GraphPermissionInfo[]>(u);
        });
        await Task.WhenAll(tasks);

        foreach (var task in tasks)
        {
            var response = await task;
            if (response is null)
            {
                continue;
            }

            // there's only one scope so it must be minimal already
            if (response.Length < 2)
            {
                continue;
            }

            if (newMinimalScopes.Contains(response[0].Value))
            {
                logger.LogDebug("Replacing scope {old} with {new}", response[0].Value, response[1].Value);
                newMinimalScopes.Remove(response[0].Value);
                newMinimalScopes.Add(response[1].Value);
            }
        }

        logger.LogDebug("Updated minimal scopes. Original: {original}, New: {new}", string.Join(", ", minimalScopes), string.Join(", ", newMinimalScopes));

        return newMinimalScopes;
    }
}