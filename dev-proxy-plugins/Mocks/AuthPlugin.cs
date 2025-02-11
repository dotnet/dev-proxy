// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft.DevProxy.Plugins.Mocks;

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

public class AuthPluginApiKeyParameter
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthPluginApiKeyIn? In { get; set; }
    public string? Name { get; set; }
}

public class AuthPluginApiKeyConfiguration
{
    public AuthPluginApiKeyParameter[]? Parameters { get; set; }
    public string[]? AllowedKeys { get; set; }
}

public class AuthPluginOAuth2Configuration
{
    public string? MetadataUrl { get; set; }
    public string[]? AllowedApplications { get; set; }
    public string[]? AllowedAudiences { get; set; }
    public string[]? AllowedPrincipals { get; set; }
    public string[]? AllowedTenants { get; set; }
    public string? Issuer { get; set; }
    public string[]? Roles { get; set; }
    public string[]? Scopes { get; set; }
    public bool ValidateLifetime { get; set; } = true;
    public bool ValidateSigningKey { get; set; } = true;
}

public class AuthPluginConfiguration
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthPluginAuthType? Type { get; set; }
    public AuthPluginApiKeyConfiguration? ApiKey { get; set; }
    public AuthPluginOAuth2Configuration? OAuth2 { get; set; }
}

public class AuthPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    override public string Name => nameof(AuthPlugin);
    private readonly AuthPluginConfiguration? _configuration = new();
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        if (_configuration is null)
        {
            return;
        }

        if (_configuration.Type == null)
        {
            Logger.LogError("Auth type is required");
            return;
        }

        if (_configuration.Type == AuthPluginAuthType.ApiKey &&
            _configuration.ApiKey is null)
        {
            Logger.LogError("ApiKey configuration is required when using ApiKey auth type");
            return;
        }

        if (_configuration.Type == AuthPluginAuthType.OAuth2 &&
            _configuration.OAuth2 is null)
        {
            Logger.LogError("OAuth2 configuration is required when using OAuth2 auth type");
            return;
        }

        if (_configuration.Type == AuthPluginAuthType.ApiKey)
        {
            Debug.Assert(_configuration.ApiKey is not null);

            if (_configuration.ApiKey.Parameters == null ||
                _configuration.ApiKey.Parameters.Length == 0)
            {
                Logger.LogError("ApiKey.Parameters is required when using ApiKey auth type");
                return;
            }

            foreach (var parameter in _configuration.ApiKey.Parameters)
            {
                if (parameter.In is null || parameter.Name is null)
                {
                    Logger.LogError("ApiKey.In and ApiKey.Name are required for each parameter");
                    return;
                }
            }

            if (_configuration.ApiKey.AllowedKeys == null || _configuration.ApiKey.AllowedKeys.Length == 0)
            {
                Logger.LogError("ApiKey.AllowedKeys is required when using ApiKey auth type");
                return;
            }
        }

        if (_configuration.Type == AuthPluginAuthType.OAuth2)
        {
            Debug.Assert(_configuration.OAuth2 is not null);

            if (string.IsNullOrWhiteSpace(_configuration.OAuth2.MetadataUrl))
            {
                Logger.LogError("OAuth2.MetadataUrl is required when using OAuth2 auth type");
                return;
            }

            await SetupOpenIdConnectConfigurationAsync();
        }

        PluginEvents.BeforeRequest += OnBeforeRequestAsync;
    }

    private async Task SetupOpenIdConnectConfigurationAsync()
    {
        try
        {
            var retriever = new OpenIdConnectConfigurationRetriever();
            var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>("https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration", retriever);
            _openIdConnectConfiguration = await configurationManager.GetConfigurationAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while loading OpenIdConnectConfiguration");
        }
    }

#pragma warning disable CS1998
    private async Task OnBeforeRequestAsync(object sender, ProxyRequestArgs e)
#pragma warning restore CS1998
    {
        if (UrlsToWatch is null || !e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (!AuthorizeRequest(e.Session))
        {
            SendUnauthorizedResponse(e.Session);
            e.ResponseState.HasBeenSet = true;
        }
        else
        {
            Logger.LogRequest("Request authorized", MessageType.Normal, new LoggingContext(e.Session));
        }
    }

    private bool AuthorizeRequest(SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.Type is not null);

        return _configuration.Type switch
        {
            AuthPluginAuthType.ApiKey => AuthorizeApiKeyRequest(session),
            AuthPluginAuthType.OAuth2 => AuthorizeOAuth2Request(session),
            _ => false,
        };
    }

    private bool AuthorizeApiKeyRequest(SessionEventArgs session)
    {
        Logger.LogDebug("Authorizing request using API key");

        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.ApiKey is not null);
        Debug.Assert(_configuration.ApiKey.AllowedKeys is not null);

        var apiKey = GetApiKey(session);
        if (apiKey is null)
        {
            Logger.LogRequest("401 Unauthorized. API key not found.", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        var isKeyValid = _configuration.ApiKey.AllowedKeys.Contains(apiKey);
        if (!isKeyValid)
        {
            Logger.LogRequest($"401 Unauthorized. API key {apiKey} is not allowed.", MessageType.Failed, new LoggingContext(session));
        }

        return isKeyValid;
    }

    private bool AuthorizeOAuth2Request(SessionEventArgs session)
    {
        Logger.LogDebug("Authorizing request using OAuth2");

        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);
        Debug.Assert(_configuration.OAuth2.MetadataUrl is not null);
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
            ValidateIssuer = !string.IsNullOrEmpty(_configuration.OAuth2.Issuer),
            ValidIssuer = _configuration.OAuth2.Issuer,
            ValidateAudience = _configuration.OAuth2.AllowedAudiences is not null && _configuration.OAuth2.AllowedAudiences.Length != 0,
            ValidAudiences = _configuration.OAuth2.AllowedAudiences,
            ValidateLifetime = _configuration.OAuth2.ValidateLifetime,
            ValidateIssuerSigningKey = _configuration.OAuth2.ValidateSigningKey
        };
        if (!_configuration.OAuth2.ValidateSigningKey)
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
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, new LoggingContext(session));
            return false;
        }
    }

    private static void SendUnauthorizedResponse(SessionEventArgs e)
    {
        SendJsonResponse("{\"error\":{\"message\":\"Unauthorized\"}}", HttpStatusCode.Unauthorized, e);
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

    #region OAuth2
    #region OAuth2 token validators

    private bool ValidatePrincipals(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);

        if (_configuration.OAuth2.AllowedPrincipals is null ||
            _configuration.OAuth2.AllowedPrincipals.Length == 0)
        {
            return true;
        }

        var principalId = claimsPrincipal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        if (principalId is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the oid claim.", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        if (!_configuration.OAuth2.AllowedPrincipals.Contains(principalId))
        {
            var principals = string.Join(", ", _configuration.OAuth2.AllowedPrincipals);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued for an allowed principal. Allowed principals: {principals}, found: {principalId}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Principal ID {principalId} is allowed", principalId);

        return true;
    }

    private bool ValidateApplications(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);

        if (_configuration.OAuth2.AllowedApplications is null ||
            _configuration.OAuth2.AllowedApplications.Length == 0)
        {
            return true;
        }

        var tokenVersion = claimsPrincipal.FindFirst("ver")?.Value;
        if (tokenVersion is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the ver claim.", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        var appId = claimsPrincipal.FindFirst(tokenVersion == "1.0" ? "appid" : "azp")?.Value;
        if (appId is null)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token doesn't have the {(tokenVersion == "v1.0" ? "appid" : "azp")} claim.", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        if (!_configuration.OAuth2.AllowedApplications.Contains(appId))
        {
            var applications = string.Join(", ", _configuration.OAuth2.AllowedApplications);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued by an allowed application. Allowed applications: {applications}, found: {appId}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Application ID {appId} is allowed", appId);

        return true;
    }

    private bool ValidateTenants(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);

        if (_configuration.OAuth2.AllowedTenants is null ||
            _configuration.OAuth2.AllowedTenants.Length == 0)
        {
            return true;
        }

        var tenantId = claimsPrincipal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        if (tenantId is null)
        {
            Logger.LogRequest("401 Unauthorized. The specified token doesn't have the tid claim.", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        if (!_configuration.OAuth2.AllowedTenants.Contains(tenantId))
        {
            var tenants = string.Join(", ", _configuration.OAuth2.AllowedTenants);
            Logger.LogRequest($"401 Unauthorized. The specified token is not issued by an allowed tenant. Allowed tenants: {tenants}, found: {tenantId}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Token issued by an allowed tenant: {tenantId}", tenantId);

        return true;
    }

    private bool ValidateRoles(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);

        if (_configuration.OAuth2.Roles is null ||
            _configuration.OAuth2.Roles.Length == 0)
        {
            return true;
        }

        var rolesFromTheToken = string.Join(' ', claimsPrincipal.Claims
            .Where(c => c.Type == ClaimTypes.Role)
            .Select(c => c.Value));

        var rolesRequired = string.Join(", ", _configuration.OAuth2.Roles);
        if (!_configuration.OAuth2.Roles.Any(r => HasPermission(r, rolesFromTheToken)))
        {
            Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Token has the necessary role(s): {rolesRequired}", rolesRequired);

        return true;
    }

    private bool ValidateScopes(ClaimsPrincipal claimsPrincipal, SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.OAuth2 is not null);

        if (_configuration.OAuth2.Scopes is null ||
            _configuration.OAuth2.Scopes.Length == 0)
        {
            return true;
        }

        var scopesFromTheToken = string.Join(' ', claimsPrincipal.Claims
            .Where(c => c.Type.Equals("http://schemas.microsoft.com/identity/claims/scope", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value));

        var scopesRequired = string.Join(", ", _configuration.OAuth2.Scopes);
        if (!_configuration.OAuth2.Scopes.Any(s => HasPermission(s, scopesFromTheToken)))
        {
            Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, new LoggingContext(session));
            return false;
        }

        Logger.LogDebug("Token has the necessary scope(s): {scopesRequired}", scopesRequired);

        return true;
    }

    #endregion

    private static bool HasPermission(string permission, string permissionString)
    {
        if (string.IsNullOrEmpty(permissionString))
        {
            return false;
        }

        var permissions = permissionString.Split(' ');
        return permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private string? GetOAuth2Token(SessionEventArgs session)
    {
        var tokenParts = session.HttpClient.Request.Headers
            .FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Split(' ');

        if (tokenParts is null)
        {
            Logger.LogRequest("401 Unauthorized. Authorization header not found.", MessageType.Failed, new LoggingContext(session));
            return null;
        }

        if (tokenParts.Length != 2 || tokenParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, new LoggingContext(session));
            return null;
        }

        return tokenParts[1];
    }

    #endregion
    #region API key

    private string? GetApiKey(SessionEventArgs session)
    {
        Debug.Assert(_configuration is not null);
        Debug.Assert(_configuration.ApiKey is not null);
        Debug.Assert(_configuration.ApiKey.Parameters is not null);

        string? apiKey = null;

        foreach (var parameter in _configuration.ApiKey.Parameters)
        {
            if (parameter.In is null || parameter.Name is null)
            {
                continue;
            }

            Logger.LogDebug("Getting API key from parameter {param} in {in}", parameter.Name, parameter.In);
            apiKey = parameter.In switch {
                AuthPluginApiKeyIn.Header => GetApiKeyFromHeader(session.HttpClient.Request, parameter.Name),
                AuthPluginApiKeyIn.Query => GetApiKeyFromQuery(session.HttpClient.Request, parameter.Name),
                AuthPluginApiKeyIn.Cookie => GetApiKeyFromCookie(session.HttpClient.Request, parameter.Name),
                _ => null
            };
            Logger.LogDebug("API key from parameter {param} in {in}: {apiKey}", parameter.Name, parameter.In, apiKey ?? "(not found)");

            if (apiKey is not null)
            {
                break;
            }
        }

        return apiKey;
    }

    private static string? GetApiKeyFromCookie(Request request, string cookieName)
    {
        var cookies = ParseCookies(request.Headers.FirstOrDefault(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))?.Value);
        if (cookies is null)
        {
            return null;
        }

        cookies.TryGetValue(cookieName, out var apiKey);
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

    #endregion
}
