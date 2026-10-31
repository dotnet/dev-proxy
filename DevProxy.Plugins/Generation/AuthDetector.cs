// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy.Http;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Web;

namespace DevProxy.Plugins.Generation;

internal enum DetectedApiKeyLocation
{
    Header,
    Query
}

internal enum DetectedOAuth2FlowType
{
    AuthorizationCode,
    ClientCredentials
}

internal abstract record DetectedAuth;

internal sealed record DetectedApiKeyAuth(string Name, DetectedApiKeyLocation In) : DetectedAuth;

internal sealed record DetectedBearerAuth(bool IsJwt = false) : DetectedAuth;

internal sealed record DetectedOAuth2Auth(
    string Name,
    DetectedOAuth2FlowType FlowType,
    string AuthorizationUrl,
    string TokenUrl,
    string RefreshUrl,
    IReadOnlyList<string> Scopes) : DetectedAuth
{
    public const string EntraName = "EntraOAuth2Auth";
}

/// <summary>
/// Detects how a recorded request authenticates, based on its headers and
/// query string. Shared by the API spec generator plugins.
/// </summary>
internal static class AuthDetector
{
    public static DetectedAuth? Detect(IHttpRequest httpRequest, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpRequest);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogTrace("Entered {Name}", nameof(Detect));

        var authHeaders = httpRequest.Headers
            .Where(h => Models.Http.AuthHeaders.Contains(h.Name.ToLowerInvariant()))
            .Select(h => (h.Name, h.Value));

        foreach (var (name, value) in authHeaders)
        {
            if (name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogTrace("Left {Name}", nameof(Detect));
                return new DetectedApiKeyAuth(name, DetectedApiKeyLocation.Header);
            }

            var bearerToken = value["Bearer ".Length..].Trim();
            logger.LogTrace("Left {Name}", nameof(Detect));
            return DetectAuthorizationAuth(bearerToken, logger);
        }

        var authQueryParam = GetAuthQueryParameter(httpRequest.RequestUri);
        if (authQueryParam is not null)
        {
            logger.LogDebug("Found auth query parameter: {AuthQueryParam}", authQueryParam);
            logger.LogTrace("Left {Name}", nameof(Detect));
            return new DetectedApiKeyAuth(authQueryParam, DetectedApiKeyLocation.Query);
        }

        logger.LogDebug("No auth headers or query parameters found");
        logger.LogTrace("Left {Name}", nameof(Detect));
        return null;
    }

    public static string? GetAuthQueryParameter(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        var query = HttpUtility.ParseQueryString(requestUri.Query);
        return query.AllKeys
            .FirstOrDefault(k => k is not null && IsAuthQueryParameter(k.ToLowerInvariant()));
    }

    // auth header names are also commonly used to pass credentials in the query string
    private static bool IsAuthQueryParameter(string lowerCaseName) =>
        Models.Http.AuthQueryParameters.Contains(lowerCaseName) ||
        Models.Http.AuthHeaders.Contains(lowerCaseName);

    private static DetectedAuth DetectAuthorizationAuth(string bearerToken, ILogger logger)
    {
        logger.LogTrace("Entered {Name}", nameof(DetectAuthorizationAuth));

        if (!IsJwtToken(bearerToken, logger, out var jwtToken))
        {
            logger.LogTrace("Left {Name}", nameof(DetectAuthorizationAuth));
            return new DetectedBearerAuth();
        }

        var issuer = jwtToken.Issuer;
        logger.LogDebug("Issuer: {Issuer}", issuer);
        if (!IsEntraToken(issuer, logger))
        {
            // the issuer doesn't identify the OAuth endpoints, so we can't
            // describe a usable OAuth2 flow
            logger.LogDebug("JWT not issued by Entra. Using bearer auth");
            logger.LogTrace("Left {Name}", nameof(DetectAuthorizationAuth));
            return new DetectedBearerAuth(IsJwt: true);
        }

        var scopes = jwtToken.Claims
            .Where(c => c.Type == "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .ToList();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Scopes: {Scopes}", string.Join(", ", scopes));
        }
        var roles = jwtToken.Claims
            .Where(c => c.Type == "roles")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .ToList();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Roles: {Roles}", string.Join(", ", roles));
        }
        // delegated tokens can also contain roles, so only treat tokens
        // without scopes as app-only
        var flowType = scopes.Count == 0 && roles.Count != 0 ?
            DetectedOAuth2FlowType.ClientCredentials : DetectedOAuth2FlowType.AuthorizationCode;
        scopes.AddRange(roles);

        var version = jwtToken.Claims
            .FirstOrDefault(c => c.Type == "ver")?.Value ?? "1.0";
        var baseUrl = issuer.Contains("v2.0", StringComparison.OrdinalIgnoreCase) ?
            issuer.Replace("v2.0", "", StringComparison.OrdinalIgnoreCase) : issuer;
        // v1 tokens are issued by sts.windows.net, but the OAuth endpoints
        // are on login.microsoftonline.com
        baseUrl = baseUrl.Replace("https://sts.windows.net/", "https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase);
        var tokenUrl = version == "1.0" ? $"{baseUrl}oauth2/token" : $"{baseUrl}oauth2/v2.0/token";
        var auth = new DetectedOAuth2Auth(
            DetectedOAuth2Auth.EntraName,
            flowType,
            version == "1.0" ? $"{baseUrl}oauth2/authorize" : $"{baseUrl}oauth2/v2.0/authorize",
            tokenUrl,
            tokenUrl,
            scopes);

        logger.LogTrace("Left {Name}", nameof(DetectAuthorizationAuth));
        return auth;
    }

    private static bool IsEntraToken(string issuer, ILogger logger)
    {
        logger.LogTrace("Entered {Name}", nameof(IsEntraToken));

        var isEntraToken = Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) &&
            issuerUri.Scheme == Uri.UriSchemeHttps &&
            issuerUri.IsDefaultPort &&
            (issuerUri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            issuerUri.Host.Equals("sts.windows.net", StringComparison.OrdinalIgnoreCase));

        logger.LogDebug("Is token from Entra? {IsEntraToken}", isEntraToken);
        logger.LogTrace("Left {Name}", nameof(IsEntraToken));

        return isEntraToken;
    }

    private static bool IsJwtToken(string bearerToken, ILogger logger, out JwtSecurityToken jwtToken)
    {
        logger.LogTrace("Entered {Name}", nameof(IsJwtToken));

        jwtToken = new();

        try
        {
            jwtToken = new(bearerToken);
            logger.LogTrace("Left {Name}", nameof(IsJwtToken));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to parse JWT token: {Ex}", ex.Message);
        }

        logger.LogTrace("Left {Name}", nameof(IsJwtToken));
        return false;
    }
}
