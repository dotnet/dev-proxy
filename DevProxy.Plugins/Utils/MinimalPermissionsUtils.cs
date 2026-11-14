// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using YamlDotNet.Core;

namespace DevProxy.Plugins.Utils;

static class MinimalPermissionsUtils
{
    /// <summary>
    /// Resolves environment variable placeholders (<c>${NAME}</c>) in an API
    /// specification and the deprecated <c>{NAME}</c> variables passed with --env.
    /// </summary>
    public static string ResolveApiSpecVariables(string fileContents, string filePath, Dictionary<string, string> legacyVariables, ILogger logger)
    {
        var unresolvedVariables = new List<string>();
        var usesLegacyVariables = false;

        // legacy {NAME} variables are replaced only in text outside of ${NAME} placeholders
        // so that escaped placeholders and resolved values aren't changed
        string replaceLegacyVariables(string text)
        {
            var replaced = ProxyUtils.ReplaceVariables(text, legacyVariables, v => $"{{{v}}}");
            usesLegacyVariables |= !string.Equals(text, replaced, StringComparison.Ordinal);
            return replaced;
        }
        Func<string, string>? transformText = legacyVariables.Count > 0 ? replaceLegacyVariables : null;

        string resolved;
        try
        {
            // resolve values in the parsed document so that values with quotes don't break it
            // and keys aren't changed
            // legacy variables were always replaced in the raw text, including keys like
            // OpenAPI paths and unquoted YAML values like `url: {NAME}`, so keep doing that
            var withLegacyVariables = transformText is null
                ? fileContents
                : EnvironmentVariablePlaceholders.TransformOutsidePlaceholders(fileContents, transformText);
            resolved = Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? EnvironmentVariablePlaceholders.ResolveInJson(withLegacyVariables, Environment.GetEnvironmentVariable, unresolvedVariables)
                : EnvironmentVariablePlaceholders.ResolveInYaml(withLegacyVariables, Environment.GetEnvironmentVariable, unresolvedVariables);
        }
        catch (Exception ex) when (ex is JsonException or YamlException)
        {
            unresolvedVariables.Clear();
            usesLegacyVariables = false;
            resolved = EnvironmentVariablePlaceholders.Resolve(fileContents, Environment.GetEnvironmentVariable, unresolvedVariables, transformText);
        }
        EnvironmentVariablePlaceholders.LogUnresolvedVariables(logger, unresolvedVariables, filePath);

        if (usesLegacyVariables)
        {
            logger.LogWarning(
                "Referencing variables using {{NAME}} in {File} is deprecated and will be removed in a future version. Use ${{NAME}} instead",
                filePath);
        }

        return resolved;
    }

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

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Scopes found in the token: {Scopes}", string.Join(", ", scopes));
            }
            return scopes;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse JWT token");
            return [];
        }
    }

    public static (IEnumerable<string> minimalScopes, IEnumerable<string> unmatchedOperations) GetMinimalScopes(IEnumerable<string> requests, Dictionary<string, string[]> operationsAndScopes)
    {
        var unmatchedOperations = requests
            .Where(o => !operationsAndScopes.Keys.Contains(o, StringComparer.OrdinalIgnoreCase));

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
                .Distinct(),
            unmatchedOperations
        );
    }
}