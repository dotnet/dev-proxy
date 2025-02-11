// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

public static class MinimalPermissionsUtils
{
    /// <summary>
    /// Gets the scopes from the JWT token.
    /// </summary>
    /// <param name="jwtToken">The JWT token including the 'Bearer' prefix.</param>
    /// <returns>The scopes from the JWT token or empty array if no scopes found or error occurred.</returns>
    public static string[] GetScopesFromToken(string? jwtToken, ILogger logger)
    {
        logger.LogDebug("Getting scopes from JWT token...");

        if (string.IsNullOrEmpty(jwtToken))
        {
            return [];
        }

        try
        {
            var token = jwtToken.Split(' ')[1];
            var handler = new JwtSecurityTokenHandler();
            var jsonToken = handler.ReadToken(token) as JwtSecurityToken;
            var scopes = jsonToken?.Claims
                .Where(c => c.Type == "scp")
                .Select(c => c.Value)
                .ToArray() ?? [];

            logger.LogDebug("Scopes found in the token: {scopes}", string.Join(", ", scopes));
            return scopes;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse JWT token");
            return [];
        }
    }

    public static (string[] minimalScopes, string[] unmatchedOperations) GetMinimalScopes(string[] requests, Dictionary<string, string[]> operationsAndScopes)
    {
        var unmatchedOperations = requests
            .Where(o => !operationsAndScopes.Keys.Contains(o, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var minimalScopesPerOperation = operationsAndScopes
            .Where(o => requests.Contains(o.Key, StringComparer.OrdinalIgnoreCase))
            .Select(o => new KeyValuePair<string, string>(o.Key, o.Value.First()))
            .ToDictionary();

        // for each minimal scope check if it overrules any other minimal scope
        // (position > 0, because the minimal scope is always first). if it does,
        // replace the minimal scope with the overruling scope
        foreach (var scope in minimalScopesPerOperation.Values)
        {
            foreach (var minimalScope in minimalScopesPerOperation)
            {
                if (Array.IndexOf(operationsAndScopes[minimalScope.Key], scope) > 0)
                {
                    minimalScopesPerOperation[minimalScope.Key] = scope;
                }
            }
        }

        return (
            minimalScopesPerOperation
                .Select(s => s.Value)
                .Distinct()
                .ToArray(),
            unmatchedOperations
        );
    }
}