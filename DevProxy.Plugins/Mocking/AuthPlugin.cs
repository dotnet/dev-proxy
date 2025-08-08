// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Unobtanium.Web.Proxy.EventArguments;
using Unobtanium.Web.Proxy.Http;
using Unobtanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

public enum AuthPluginAuthType
{
    ApiKey,
    OAuth2
}

public enum AuthPluginApiKeyIn
{
    Header,
    Query,
    Cookie
}

public sealed class AuthPluginApiKeyParameter
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthPluginApiKeyIn? In { get; set; }
    public string? Name { get; set; }
}

public sealed class AuthPluginApiKeyConfiguration
{
    public IEnumerable<string>? AllowedKeys { get; set; }
    public IEnumerable<AuthPluginApiKeyParameter>? Parameters { get; set; }
}

public sealed class AuthPluginOAuth2Configuration
{
    public IEnumerable<string>? AllowedApplications { get; set; }
    public IEnumerable<string>? AllowedAudiences { get; set; }
    public IEnumerable<string>? AllowedPrincipals { get; set; }
    public IEnumerable<string>? AllowedTenants { get; set; }
    public string? Issuer { get; set; }
    public string? MetadataUrl { get; set; }
    public IEnumerable<string>? Roles { get; set; }
    public IEnumerable<string>? Scopes { get; set; }
    public bool ValidateLifetime { get; set; } = true;
    public bool ValidateSigningKey { get; set; } = true;
}

public sealed class AuthPluginConfiguration
{
    public AuthPluginApiKeyConfiguration? ApiKey { get; set; }
    public AuthPluginOAuth2Configuration? OAuth2 { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthPluginAuthType? Type { get; set; }
}

public sealed class AuthPlugin(
    HttpClient httpClient,
    ILogger<AuthPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<AuthPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override string Name => nameof(AuthPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        if (Configuration.Type == null)
        {
            Logger.LogError("Auth type is required");
            return;
        }

        if (Configuration.Type == AuthPluginAuthType.ApiKey &&
            Configuration.ApiKey is null)
        {
            Logger.LogError("ApiKey configuration is required when using ApiKey auth type");
            return;
        }

        if (Configuration.Type == AuthPluginAuthType.OAuth2 &&
            Configuration.OAuth2 is null)
        {
            Logger.LogError("OAuth2 configuration is required when using OAuth2 auth type");
            return;
        }

        if (Configuration.Type == AuthPluginAuthType.ApiKey)
        {
            Debug.Assert(Configuration.ApiKey is not null);

            if (Configuration.ApiKey.Parameters == null ||
                Configuration.ApiKey.Parameters.Any())
            {
                Logger.LogError("ApiKey.Parameters is required when using ApiKey auth type");
                return;
            }

            foreach (var parameter in Configuration.ApiKey.Parameters)
            {
                if (parameter.In is null || parameter.Name is null)
                {
                    Logger.LogError("ApiKey.In and ApiKey.Name are required for each parameter");
                    return;
                }
            }

            if (Configuration.ApiKey.AllowedKeys == null ||
                !Configuration.ApiKey.AllowedKeys.Any())
            {
                Logger.LogError("ApiKey.AllowedKeys is required when using ApiKey auth type");
                return;
            }
        }

        if (Configuration.Type == AuthPluginAuthType.OAuth2)
        {
            Debug.Assert(Configuration.OAuth2 is not null);

            if (string.IsNullOrWhiteSpace(Configuration.OAuth2.MetadataUrl))
            {
                Logger.LogError("OAuth2.MetadataUrl is required when using OAuth2 auth type");
                return;
            }

            await SetupOpenIdConnectConfigurationAsync(Configuration.OAuth2.MetadataUrl);
        }

        Enabled = false;
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        if (!AuthorizeRequest(e.Session))
        {
            SendUnauthorizedResponse(e.Session);
            e.ResponseState.HasBeenSet = true;
        }
        else
        {
            Logger.LogRequest("Request authorized", MessageType.Normal, new(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    private async Task SetupOpenIdConnectConfigurationAsync(string metadataUrl)
    {
        try
        {
            var retriever = new OpenIdConnectConfigurationRetriever();
            var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(metadataUrl, retriever);
            _openIdConnectConfiguration = await configurationManager.GetConfigurationAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while loading OpenIdConnectConfiguration");
        }
    }

    private bool AuthorizeRequest(SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.Type is not null);

        return Configuration.Type switch
        {
            AuthPluginAuthType.ApiKey => AuthorizeApiKeyRequest(session),
            AuthPluginAuthType.OAuth2 => AuthorizeOAuth2Request(session),
            _ => false,
        };
    }

    private bool AuthorizeApiKeyRequest(SessionEventArgs session)
    {
        Logger.LogDebug("Authorizing request using API key");

        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.ApiKey is not null);
        Debug.Assert(Configuration.ApiKey.AllowedKeys is not null);

        var apiKey = GetApiKey(session);
        if (apiKey is null)
        {
            Logger.LogRequest("401 Unauthorized. API key not found.", MessageType.Failed, new(session));
            return false;
        }

        var isKeyValid = Configuration.ApiKey.AllowedKeys.Contains(apiKey);
        if (!isKeyValid)
        {
            Logger.LogRequest($"401 Unauthorized. API key {apiKey} is not allowed.", MessageType.Failed, new(session));
        }

        return isKeyValid;
    }

    private bool AuthorizeOAuth2Request(SessionEventArgs session)
    {
        Logger.LogDebug("Authorizing request using OAuth2");

        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);
        Debug.Assert(Configuration.OAuth2.MetadataUrl is not null);
        Debug.Assert(_openIdConnectConfiguration is not null);

        var token = GetOAuth2Token(session);
        if (token is null)
        {
            return false;
        }

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            IssuerSigningKeys = _openIdConnectConfiguration?.SigningKeys,
            ValidateIssuer = !string.IsNullOrEmpty(Configuration.OAuth2.Issuer),
            ValidIssuer = Configuration.OAuth2.Issuer,
            ValidateAudience = Configuration.OAuth2.AllowedAudiences is not null && Configuration.OAuth2.AllowedAudiences.Any(),
            ValidAudiences = Configuration.OAuth2.AllowedAudiences,
            ValidateLifetime = Configuration.OAuth2.ValidateLifetime,
            ValidateIssuerSigningKey = Configuration.OAuth2.ValidateSigningKey
        };
        if (!Configuration.OAuth2.ValidateSigningKey)
        {
            // suppress token validation
            validationParameters.SignatureValidator = delegate (string token, TokenValidationParameters parameters)
            {
                return new JwtSecurityToken(token);
            };
        }

        try
        {
            var claimsPrincipal = handler.ValidateToken(token, validationParameters, out _);
            return ValidateTenants(claimsPrincipal, session) &&
                ValidateApplications(claimsPrincipal, session) &&
                ValidatePrincipals(claimsPrincipal, session) &&
                ValidateRoles(claimsPrincipal, session) &&
                ValidateScopes(claimsPrincipal, session);
        }
        catch (Exception ex)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, new(session));
            return false;
        }
    }

    private bool ValidatePrincipals(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);

        if (Configuration.OAuth2.AllowedPrincipals is null ||
            Configuration.OAuth2.AllowedPrincipals.Any())
        {
            return true;
        }

        var principalId = claimsPrincipal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        if (principalId is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the oid claim.", MessageType.Failed, new(session));
            return false;
        }

        if (!Configuration.OAuth2.AllowedPrincipals.Contains(principalId))
        {
            var principals = string.Join(", ", Configuration.OAuth2.AllowedPrincipals);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued for an allowed principal. Allowed principals: {principals}, found: {principalId}", MessageType.Failed, new(session));
            return false;
        }

        Logger.LogDebug("Principal ID {PrincipalId} is allowed", principalId);

        return true;
    }

    private bool ValidateApplications(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);

        if (Configuration.OAuth2.AllowedApplications is null ||
            Configuration.OAuth2.AllowedApplications.Any())
        {
            return true;
        }

        var tokenVersion = claimsPrincipal.FindFirst("ver")?.Value;
        if (tokenVersion is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the ver claim.", MessageType.Failed, new(session));
            return false;
        }

        var appId = claimsPrincipal.FindFirst(tokenVersion == "1.0" ? "appid" : "azp")?.Value;
        if (appId is null)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token doesn't have the {(tokenVersion == "v1.0" ? "appid" : "azp")} claim.", MessageType.Failed, new(session));
            return false;
        }

        if (!Configuration.OAuth2.AllowedApplications.Contains(appId))
        {
            var applications = string.Join(", ", Configuration.OAuth2.AllowedApplications);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued by an allowed application. Allowed applications: {applications}, found: {appId}", MessageType.Failed, new(session));
            return false;
        }

        Logger.LogDebug("Application ID {AppId} is allowed", appId);

        return true;
    }

    private bool ValidateTenants(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);

        if (Configuration.OAuth2.AllowedTenants is null ||
            Configuration.OAuth2.AllowedTenants.Any())
        {
            return true;
        }

        var tenantId = claimsPrincipal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        if (tenantId is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the tid claim.", MessageType.Failed, new(session));
            return false;
        }

        if (!Configuration.OAuth2.AllowedTenants.Contains(tenantId))
        {
            var tenants = string.Join(", ", Configuration.OAuth2.AllowedTenants);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued by an allowed tenant. Allowed tenants: {tenants}, found: {tenantId}", MessageType.Failed, new(session));
            return false;
        }

        Logger.LogDebug("Token issued by an allowed tenant: {TenantId}", tenantId);

        return true;
    }

    private bool ValidateRoles(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);

        if (Configuration.OAuth2.Roles is null ||
            Configuration.OAuth2.Roles.Any())
        {
            return true;
        }

        var rolesFromTheToken = string.Join(' ', claimsPrincipal.Claims
            .Where(c => c.Type == ClaimTypes.Role)
            .Select(c => c.Value));

        var rolesRequired = string.Join(", ", Configuration.OAuth2.Roles);
        if (!Configuration.OAuth2.Roles.Any(r => HasPermission(r, rolesFromTheToken)))
        {
            Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, new(session));
            return false;
        }

        Logger.LogDebug("Token has the necessary role(s): {RolesRequired}", rolesRequired);

        return true;
    }

    private bool ValidateScopes(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.OAuth2 is not null);

        if (Configuration.OAuth2.Scopes is null ||
            Configuration.OAuth2.Scopes.Any())
        {
            return true;
        }

        var scopesFromTheToken = string.Join(' ', claimsPrincipal.Claims
            .Where(c => c.Type.Equals("http://schemas.microsoft.com/identity/claims/scope", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value));

        var scopesRequired = string.Join(", ", Configuration.OAuth2.Scopes);
        if (!Configuration.OAuth2.Scopes.Any(s => HasPermission(s, scopesFromTheToken)))
        {
            Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Token has the necessary scope(s): {ScopesRequired}", scopesRequired);

        return true;
    }

    private string? GetOAuth2Token(SessionEventArgs session)
    {
        var tokenParts = session.HttpClient.Request.Headers
            .FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Split(' ');

        if (tokenParts is null)
        {
            Logger.LogRequest("401 Unauthorized. Authorization header not found.", MessageType.Failed, new(session));
            return null;
        }

        if (tokenParts.Length != 2 || tokenParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, new(session));
            return null;
        }

        return tokenParts[1];
    }

    private string? GetApiKey(SessionEventArgs session)
    {
        Debug.Assert(Configuration is not null);
        Debug.Assert(Configuration.ApiKey is not null);
        Debug.Assert(Configuration.ApiKey.Parameters is not null);

        string? apiKey = null;

        foreach (var parameter in Configuration.ApiKey.Parameters)
        {
            if (parameter.In is null || parameter.Name is null)
            {
                continue;
            }

            Logger.LogDebug("Getting API key from parameter {Param} in {In}", parameter.Name, parameter.In);
            apiKey = parameter.In switch
            {
                AuthPluginApiKeyIn.Header => GetApiKeyFromHeader(session.HttpClient.Request, parameter.Name),
                AuthPluginApiKeyIn.Query => GetApiKeyFromQuery(session.HttpClient.Request, parameter.Name),
                AuthPluginApiKeyIn.Cookie => GetApiKeyFromCookie(session.HttpClient.Request, parameter.Name),
                _ => null
            };
            Logger.LogDebug("API key from parameter {Param} in {In}: {ApiKey}", parameter.Name, parameter.In, apiKey ?? "(not found)");

            if (apiKey is not null)
            {
                break;
            }
        }

        return apiKey;
    }

    private static void SendUnauthorizedResponse(SessionEventArgs e)
    {
        var body = new
        {
            error = new
            {
                message = "Unauthorized"
            }
        };
        SendJsonResponse(JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.Unauthorized, e);
    }

    private static void SendJsonResponse(string body, HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader> {
            new("content-type", "application/json; charset=utf-8")
        };
        if (e.HttpClient.Request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("access-control-allow-origin", "*"));
        }
        e.GenericResponse(body, statusCode, headers);
    }

    private static bool HasPermission(string permission, string permissionString)
    {
        if (string.IsNullOrEmpty(permissionString))
        {
            return false;
        }

        var permissions = permissionString.Split(' ');
        return permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetApiKeyFromCookie(Request request, string cookieName)
    {
        var cookies = ParseCookies(request.Headers.FirstOrDefault(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))?.Value);
        if (cookies is null)
        {
            return null;
        }

        _ = cookies.TryGetValue(cookieName, out var apiKey);
        return apiKey;
    }

    private static Dictionary<string, string>? ParseCookies(string? cookieHeader)
    {
        if (cookieHeader is null)
        {
            return null;
        }

        var cookies = new Dictionary<string, string>();
        foreach (var cookie in cookieHeader.Split(';'))
        {
            var parts = cookie.Split('=');
            if (parts.Length == 2)
            {
                cookies[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return cookies;
    }

    private static string? GetApiKeyFromQuery(Request request, string paramName)
    {
        var queryParameters = HttpUtility.ParseQueryString(request.RequestUri.Query);
        return queryParameters[paramName];
    }

    private static string? GetApiKeyFromHeader(Request request, string headerName)
    {
        return request.Headers.FirstOrDefault(h => h.Name == headerName)?.Value;
    }
}
