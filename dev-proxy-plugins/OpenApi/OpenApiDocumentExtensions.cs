// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.MinimalPermissions;
using Microsoft.Extensions.Logging;

#pragma warning disable IDE0130
namespace Microsoft.OpenApi.Models;
#pragma warning restore IDE0130


public static class OpenApiDocumentExtensions
{
    public static KeyValuePair<string, OpenApiPathItem>? FindMatchingPathItem(this OpenApiDocument openApiDocument, string requestUrl, ILogger logger)
    {
        foreach (var server in openApiDocument.Servers)
        {
            logger.LogDebug("Checking server URL {serverUrl}...", server.Url);

            if (!requestUrl.StartsWith(server.Url, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Request URL {requestUrl} does not match server URL {serverUrl}", requestUrl, server.Url);
                continue;
            }

            var serverUrl = new Uri(server.Url);
            var serverPath = serverUrl.AbsolutePath.TrimEnd('/');
            var requestUri = new Uri(requestUrl);
            var urlPathFromRequest = requestUri.GetLeftPart(UriPartial.Path).Replace(server.Url.TrimEnd('/'), "", StringComparison.OrdinalIgnoreCase);

            foreach (var path in openApiDocument.Paths)
            {
                var urlPathFromSpec = path.Key;
                logger.LogDebug("Checking path {urlPath}...", urlPathFromSpec);

                // check if path contains parameters. If it does,
                // replace them with regex
                if (urlPathFromSpec.Contains('{'))
                {
                    logger.LogDebug("Path {urlPath} contains parameters and will be converted to Regex", urlPathFromSpec);

                    // force replace all parameters with regex
                    // this is more robust than replacing parameters by name
                    // because it's possible to define parameters both on the path
                    // and operations and sometimes, parameters are defined only
                    // on the operation. This way, we cover all cases, and we don't
                    // care about the parameter anyway here
                    urlPathFromSpec = Regex.Replace(urlPathFromSpec, @"\{[^}]+\}", $"([^/]+)");

                    logger.LogDebug("Converted path to Regex: {urlPath}", urlPathFromSpec);
                    var regex = new Regex($"^{urlPathFromSpec}$");
                    if (regex.IsMatch(urlPathFromRequest))
                    {
                        logger.LogDebug("Regex matches {requestUrl}", urlPathFromRequest);

                        return path;
                    }

                    logger.LogDebug("Regex does not match {requestUrl}", urlPathFromRequest);
                }
                else
                {
                    if (urlPathFromRequest.Equals(urlPathFromSpec, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogDebug("{requestUrl} matches {urlPath}", requestUrl, urlPathFromSpec);
                        return path;
                    }

                    logger.LogDebug("{requestUrl} doesn't match {urlPath}", requestUrl, urlPathFromSpec);
                }
            }
        }

        return null;
    }

    public static string[] GetEffectiveScopes(this OpenApiOperation operation, OpenApiDocument openApiDocument, ILogger logger)
    {
        var oauth2Scheme = openApiDocument.GetOAuth2Schemes().FirstOrDefault();
        if (oauth2Scheme is null)
        {
            logger.LogDebug("No OAuth2 schemes found in OpenAPI document");
            return [];
        }

        var globalScopes = Array.Empty<string>();
        var globalOAuth2Requirement = openApiDocument.SecurityRequirements
            .FirstOrDefault(req => req.ContainsKey(oauth2Scheme));
        if (globalOAuth2Requirement is not null)
        {
            globalScopes = [.. globalOAuth2Requirement[oauth2Scheme]];
        }

        if (operation.Security is null)
        {
            logger.LogDebug("No security requirements found in operation {operation}", operation.OperationId);
            return globalScopes;
        }

        var operationOAuth2Requirement = operation.Security
            .Where(req => req.ContainsKey(oauth2Scheme))
            .SelectMany(req => req[oauth2Scheme]);
        if (operationOAuth2Requirement is not null)
        {
            return operationOAuth2Requirement.ToArray();
        }

        return [];
    }

    public static OpenApiSecurityScheme[] GetOAuth2Schemes(this OpenApiDocument openApiDocument)
    {
        return openApiDocument.Components.SecuritySchemes
            .Where(s => s.Value.Type == SecuritySchemeType.OAuth2)
            .Select(s => s.Value)
            .ToArray();
    }

    public static ApiPermissionsInfo CheckMinimalPermissions(this OpenApiDocument openApiDocument, IEnumerable<RequestLog> requests, ILogger logger)
    {
        logger.LogInformation("Checking minimal permissions for API {apiName}...", openApiDocument.Servers.First().Url);

        var tokenPermissions = new List<string>();
        var operationsFromRequests = new List<ApiOperation>();
        var operationsAndScopes = new Dictionary<string, string[]>();
        var errors = new List<ApiPermissionError>();

        foreach (var request in requests)
        {
            // get scopes from the token
            var methodAndUrl = request.Message;
            var methodAndUrlChunks = methodAndUrl.Split(' ');
            logger.LogDebug("Checking request {request}...", methodAndUrl);
            var (method, url) = (methodAndUrlChunks[0].ToUpper(), methodAndUrlChunks[1]);

            var scopesFromTheToken = MinimalPermissionsUtils.GetScopesFromToken(request.Context?.Session.HttpClient.Request.Headers.First(h => h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value, logger);
            if (scopesFromTheToken.Length != 0)
            {
                tokenPermissions.AddRange(scopesFromTheToken);
            }
            else
            {
                errors.Add(new()
                {
                    Request = methodAndUrl,
                    Error = "No scopes found in the token"
                });
            }

            // get allowed scopes for the operation
            if (!Enum.TryParse<OperationType>(method, true, out var operationType))
            {
                errors.Add(new()
                {
                    Request = methodAndUrl,
                    Error = $"{method} is not a valid HTTP method"
                });
                continue;
            }

            var pathItem = openApiDocument.FindMatchingPathItem(url, logger);
            if (pathItem is null)
            {
                errors.Add(new()
                {
                    Request = methodAndUrl,
                    Error = "No matching path item found"
                });
                continue;
            }

            if (!pathItem.Value.Value.Operations.TryGetValue(operationType, out var operation))
            {
                errors.Add(new()
                {
                    Request = methodAndUrl,
                    Error = "No matching operation found"
                });
                continue;
            }

            var scopes = operation.GetEffectiveScopes(openApiDocument, logger);
            if (scopes.Length != 0)
            {
                operationsAndScopes[$"{method} {pathItem.Value.Key}"] = scopes;
            }

            operationsFromRequests.Add(new()
            {
                Method = operationType.ToString().ToUpper(),
                OriginalUrl = url,
                TokenizedUrl = pathItem.Value.Key
            });
        }

        var (minimalScopes, unmatchedOperations) = MinimalPermissionsUtils.GetMinimalScopes(
            operationsFromRequests
                .Select(o => $"{o.Method} {o.TokenizedUrl}")
                .Distinct()
                .ToArray(),
            operationsAndScopes
        );

        var permissionsInfo = new ApiPermissionsInfo
        {
            TokenPermissions = tokenPermissions,
            OperationsFromRequests = operationsFromRequests,
            MinimalScopes = minimalScopes,
            UnmatchedOperations = unmatchedOperations,
            Errors = errors
        };

        return permissionsInfo;
    }
}