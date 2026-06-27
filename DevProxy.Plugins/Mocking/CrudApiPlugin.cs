// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Mocking;

public enum CrudApiActionType
{
    Create,
    GetAll,
    GetOne,
    GetMany,
    Merge,
    Update,
    Delete
}

public enum CrudApiAuthType
{
    None,
    Entra,
    ApiKey
}

public sealed class CrudApiEntraAuth
{
    public string Audience { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public IEnumerable<string> Roles { get; set; } = [];
    public IEnumerable<string> Scopes { get; set; } = [];
    public bool ValidateLifetime { get; set; }
    public bool ValidateSigningKey { get; set; }
}

public sealed class CrudApiApiKeyAuth
{
    public string ApiKey { get; set; } = string.Empty;
    public string? HeaderName { get; set; }
    public string? QueryParameterName { get; set; }
}

public sealed class CrudApiAction
{
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiActionType Action { get; set; } = CrudApiActionType.GetAll;
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiAuthType Auth { get; set; } = CrudApiAuthType.None;
    public CrudApiEntraAuth? EntraAuthConfig { get; set; }
    public string? Method { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class CrudApiConfiguration
{
    public IEnumerable<CrudApiAction> Actions { get; set; } = [];
    public CrudApiApiKeyAuth? ApiKeyAuthConfig { get; set; }
    public string ApiFile { get; set; } = "api.json";
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiAuthType Auth { get; set; } = CrudApiAuthType.None;
    public string BaseUrl { get; set; } = string.Empty;
    public string DataFile { get; set; } = string.Empty;
    [JsonPropertyName("enableCors")]
    public bool EnableCORS { get; set; } = true;
    public CrudApiEntraAuth? EntraAuthConfig { get; set; }
}

public sealed class CrudApiPlugin(
    HttpClient httpClient,
    ILogger<CrudApiPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<CrudApiConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private CrudApiDefinitionLoader? _definitionLoader;
    private CrudApiDataLoader? _dataLoader;
    private JArray? _data;
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override string Name => nameof(CrudApiPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        Configuration.ApiFile = ProxyUtils.GetFullPath(Configuration.ApiFile, ProxyConfiguration.ConfigFile);

        _definitionLoader = ActivatorUtilities.CreateInstance<CrudApiDefinitionLoader>(e.ServiceProvider, Configuration);
        await _definitionLoader.InitFileWatcherAsync(cancellationToken);

        if (Configuration.Auth == CrudApiAuthType.Entra &&
            Configuration.EntraAuthConfig is null)
        {
            Logger.LogError("Entra auth is enabled but no configuration is provided. API will work anonymously.");
            Configuration.Auth = CrudApiAuthType.None;
        }

        if (Configuration.Auth == CrudApiAuthType.ApiKey &&
            Configuration.ApiKeyAuthConfig is null)
        {
            Logger.LogError("API Key auth is enabled but no configuration is provided. API will work anonymously.");
            Configuration.Auth = CrudApiAuthType.None;
        }

        if (Configuration.Auth == CrudApiAuthType.ApiKey &&
            Configuration.ApiKeyAuthConfig is not null &&
            string.IsNullOrEmpty(Configuration.ApiKeyAuthConfig.ApiKey))
        {
            Logger.LogError("API Key auth is enabled but no API key is configured. API will work anonymously.");
            Configuration.Auth = CrudApiAuthType.None;
        }

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, Configuration.BaseUrl, true))
        {
            Logger.LogWarning(
                "The base URL of the API {BaseUrl} does not match any URL to watch. The {Plugin} plugin will be disabled. To enable it, add {Url}* to the list of URLs to watch and restart Dev Proxy.",
                Configuration.BaseUrl,
                Name,
                Configuration.BaseUrl
            );
            Enabled = false;
            return;
        }

        _dataLoader = ActivatorUtilities.CreateInstance<CrudApiDataLoader>(
            e.ServiceProvider,
            Configuration,
            (Action<JArray?>)(data => _data = data)
        );
        await _dataLoader.InitFileWatcherAsync(cancellationToken);

        await SetupOpenIdConnectConfigurationAsync();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.ProxySession.Request;
        var state = e.ResponseState;

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        if (IsCORSPreflightRequest(request) && Configuration.EnableCORS)
        {
            SendEmptyResponse(HttpStatusCode.NoContent, e.ProxySession);
            Logger.LogRequest("CORS preflight request", MessageType.Mocked, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        if (!AuthorizeRequest(e))
        {
            SendUnauthorizedResponse(e.ProxySession);
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }

        var actionAndParams = GetMatchingActionHandler(request);
        if (actionAndParams is not null)
        {
            if (!AuthorizeRequest(e, actionAndParams.Value.action))
            {
                SendUnauthorizedResponse(e.ProxySession);
                state.HasBeenSet = true;
                return Task.CompletedTask;
            }

            actionAndParams.Value.handler(e, actionAndParams.Value.action, actionAndParams.Value.parameters);
            state.HasBeenSet = true;
        }
        else
        {
            Logger.LogRequest("Did not match any action", MessageType.Skipped, new LoggingContext(e.ProxySession));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
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

    private (Action<ProxyRequestArgs, CrudApiAction, IDictionary<string, string>> handler, CrudApiAction action, IDictionary<string, string> parameters)? GetMatchingActionHandler(IHttpRequest request)
    {
        if (Configuration.Actions is null ||
            !Configuration.Actions.Any())
        {
            return null;
        }

        var parameterMatchEvaluator = new MatchEvaluator(m =>
        {
            var paramName = m.Value.Trim('{', '}').Replace('-', '_');
            return $"(?<{paramName}>[^/&]+)";
        });

        var requestUrlWithoutQuery = request.RequestUri.GetLeftPart(UriPartial.Path);
        var parameters = new Dictionary<string, string>();
        var action = Configuration.Actions.FirstOrDefault(action =>
        {
            if (action.Method != request.Method)
            {
                return false;
            }

            var absoluteActionUrl = (Configuration.BaseUrl + action.Url).Replace("//", "/", 8);

            if (absoluteActionUrl == requestUrlWithoutQuery)
            {
                return true;
            }

            // check if the action contains parameters
            // if it doesn't, it's not a match for the current request for sure
            if (!absoluteActionUrl.Contains('{', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // convert parameters into named regex groups
            var urlRegex = Regex.Replace(Regex.Escape(absoluteActionUrl).Replace("\\{", "{", StringComparison.OrdinalIgnoreCase), "({[^}]+})", parameterMatchEvaluator);
            var match = Regex.Match(requestUrlWithoutQuery, urlRegex);
            if (!match.Success)
            {
                return false;
            }

            foreach (var groupName in match.Groups.Keys)
            {
                if (groupName == "0")
                {
                    continue;
                }
                parameters.Add(groupName, Uri.UnescapeDataString(match.Groups[groupName].Value));
            }
            return true;
        });

        if (action is null)
        {
            return null;
        }

        return (handler: action.Action switch
        {
            CrudApiActionType.Create => Create,
            CrudApiActionType.GetAll => GetAll,
            CrudApiActionType.GetOne => GetOne,
            CrudApiActionType.GetMany => GetMany,
            CrudApiActionType.Merge => Merge,
            CrudApiActionType.Update => Update,
            CrudApiActionType.Delete => Delete,
            _ => throw new NotImplementedException()
        }, action, parameters);
    }

    private void AddCORSHeaders(IHttpRequest request, List<HttpHeader> headers)
    {
        var origin = request.Headers.FirstOrDefault(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        headers.Add(new HttpHeader("access-control-allow-origin", origin));

        var allowHeaders = new List<string> { "content-type" };

        if (Configuration.EntraAuthConfig is not null ||
            Configuration.Actions.Any(a => a.Auth == CrudApiAuthType.Entra))
        {
            allowHeaders.Add("authorization");
        }

        if (Configuration.ApiKeyAuthConfig is not null &&
            !string.IsNullOrEmpty(Configuration.ApiKeyAuthConfig.HeaderName))
        {
            if (!allowHeaders.Contains(Configuration.ApiKeyAuthConfig.HeaderName, StringComparer.OrdinalIgnoreCase))
            {
                allowHeaders.Add(Configuration.ApiKeyAuthConfig.HeaderName);
            }
        }

        headers.Add(new HttpHeader("access-control-allow-headers", string.Join(", ", allowHeaders)));

        var methods = string.Join(", ", Configuration.Actions
            .Where(a => a.Method is not null)
            .Select(a => a.Method)
            .Distinct());

        headers.Add(new HttpHeader("access-control-allow-methods", methods));
    }

    private bool AuthorizeRequest(ProxyRequestArgs e, CrudApiAction? action = null)
    {
        var authType = action is null ? Configuration.Auth : action.Auth;

        if (authType == CrudApiAuthType.None)
        {
            if (action is null)
            {
                Logger.LogDebug("No auth is required for this API.");
            }
            return true;
        }

        if (authType == CrudApiAuthType.ApiKey)
        {
            return AuthorizeApiKeyRequest(e);
        }

        return AuthorizeEntraRequest(e, action);
    }

    private bool AuthorizeApiKeyRequest(ProxyRequestArgs e)
    {
        var apiKeyAuthConfig = Configuration.ApiKeyAuthConfig;

        Debug.Assert(apiKeyAuthConfig is not null, "ApiKeyAuthConfig is null when API key auth is required.");

        // Check header
        if (!string.IsNullOrEmpty(apiKeyAuthConfig.HeaderName))
        {
            var headerValue = e.ProxySession.Request.Headers
                .FirstOrDefault(h => h.Name.Equals(apiKeyAuthConfig.HeaderName, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(headerValue) && headerValue == apiKeyAuthConfig.ApiKey)
            {
                return true;
            }
        }

        // Check query parameter
        if (!string.IsNullOrEmpty(apiKeyAuthConfig.QueryParameterName))
        {
            var requestUrl = e.ProxySession.Request.RequestUri;
            var queryString = requestUrl.Query;
            if (!string.IsNullOrEmpty(queryString))
            {
                var queryParams = System.Web.HttpUtility.ParseQueryString(queryString);
                var queryValue = queryParams[apiKeyAuthConfig.QueryParameterName];
                if (!string.IsNullOrEmpty(queryValue) && queryValue == apiKeyAuthConfig.ApiKey)
                {
                    return true;
                }
            }
        }

        Logger.LogRequest("401 Unauthorized. The specified API key is not valid.", MessageType.Failed, new LoggingContext(e.ProxySession));
        return false;
    }

    private bool AuthorizeEntraRequest(ProxyRequestArgs e, CrudApiAction? action = null)
    {
        var authConfig = action is null ? Configuration.EntraAuthConfig : action.EntraAuthConfig;

        Debug.Assert(authConfig is not null, "EntraAuthConfig is null when auth is required.");

        var token = e.ProxySession.Request.Headers.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))?.Value;
        // is there a token
        if (string.IsNullOrEmpty(token))
        {
            Logger.LogRequest("401 Unauthorized. No token found on the request.", MessageType.Failed, new LoggingContext(e.ProxySession));
            return false;
        }

        // does the token has a valid format
        var tokenHeaderParts = token.Split(' ');
        if (tokenHeaderParts.Length != 2 || tokenHeaderParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, new LoggingContext(e.ProxySession));
            return false;
        }

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            IssuerSigningKeys = _openIdConnectConfiguration?.SigningKeys,
            ValidateIssuer = !string.IsNullOrEmpty(authConfig.Issuer),
            ValidIssuer = authConfig.Issuer,
            ValidateAudience = !string.IsNullOrEmpty(authConfig.Audience),
            ValidAudience = authConfig.Audience,
            ValidateLifetime = authConfig.ValidateLifetime,
            ValidateIssuerSigningKey = authConfig.ValidateSigningKey
        };
        if (!authConfig.ValidateSigningKey)
        {
            // suppress token validation
            validationParameters.SignatureValidator = delegate (string token, TokenValidationParameters parameters)
            {
                var jwt = new JwtSecurityToken(token);
                return jwt;
            };
        }

        try
        {
            var claimsPrincipal = handler.ValidateToken(tokenHeaderParts[1], validationParameters, out _);

            // does the token has valid roles/scopes
            if (authConfig.Roles.Any())
            {
                var rolesFromTheToken = string.Join(' ', claimsPrincipal.Claims
                    .Where(c => c.Type == ClaimTypes.Role)
                    .Select(c => c.Value));

                if (!authConfig.Roles.Any(r => HasPermission(r, rolesFromTheToken)))
                {
                    var rolesRequired = string.Join(", ", authConfig.Roles);

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, new LoggingContext(e.ProxySession));
                    return false;
                }

                return true;
            }
            if (authConfig.Scopes.Any())
            {
                var scopesFromTheToken = string.Join(' ', claimsPrincipal.Claims
                    .Where(c => c.Type == "http://schemas.microsoft.com/identity/claims/scope")
                    .Select(c => c.Value));

                if (!authConfig.Scopes.Any(s => HasPermission(s, scopesFromTheToken)))
                {
                    var scopesRequired = string.Join(", ", authConfig.Scopes);

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, new LoggingContext(e.ProxySession));
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, new LoggingContext(e.ProxySession));
            return false;
        }

        return true;
    }

    private void SendUnauthorizedResponse(IProxySession session)
    {
        var body = new
        {
            error = new
            {
                message = "Unauthorized"
            }
        };
        SendJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.Unauthorized, session);
    }

    private void SendNotFoundResponse(IProxySession session)
    {
        var body = new
        {
            error = new
            {
                message = "Not found"
            }
        };
        SendJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.NotFound, session);
    }

    private void SendEmptyResponse(HttpStatusCode statusCode, IProxySession session)
    {
        var headers = new List<HttpHeader>();
        AddCORSHeaders(session.Request, headers);
        session.Respond("", statusCode, headers);
    }

    private void SendJsonResponse(string body, HttpStatusCode statusCode, IProxySession session)
    {
        var headers = new List<HttpHeader> {
            new("content-type", "application/json; charset=utf-8")
        };
        AddCORSHeaders(session.Request, headers);
        session.Respond(body, statusCode, headers);
    }

    private void GetAll(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        SendJsonResponse(JsonConvert.SerializeObject(_data, Formatting.Indented), HttpStatusCode.OK, e.ProxySession);
        Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
    }

    private void GetOne(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e.ProxySession);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
                return;
            }

            SendJsonResponse(JsonConvert.SerializeObject(item, Formatting.Indented), HttpStatusCode.OK, e.ProxySession);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private void GetMany(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var items = (_data?.SelectTokens(ReplaceParams(action.Query, parameters))) ?? [];
            SendJsonResponse(JsonConvert.SerializeObject(items, Formatting.Indented), HttpStatusCode.OK, e.ProxySession);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private void Create(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var data = JObject.Parse(e.ProxySession.Request.BodyString);
            _data?.Add(data);
            SendJsonResponse(JsonConvert.SerializeObject(data, Formatting.Indented), HttpStatusCode.Created, e.ProxySession);
            Logger.LogRequest($"201 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private void Merge(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e.ProxySession);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
                return;
            }
            var update = JObject.Parse(e.ProxySession.Request.BodyString);
            ((JContainer)item).Merge(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e.ProxySession);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private void Update(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e.ProxySession);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
                return;
            }
            var update = JObject.Parse(e.ProxySession.Request.BodyString);
            ((JContainer)item).Replace(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e.ProxySession);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private void Delete(ProxyRequestArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e.ProxySession);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
                return;
            }

            item.Remove();
            SendEmptyResponse(HttpStatusCode.NoContent, e.ProxySession);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e.ProxySession));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e.ProxySession);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e.ProxySession));
        }
    }

    private static bool IsCORSPreflightRequest(IHttpRequest request)
    {
        return request.Method == "OPTIONS" &&
               request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase));
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

    private static string ReplaceParams(string query, IDictionary<string, string> parameters)
    {
        var result = Regex.Replace(query, "{([^}]+)}", new MatchEvaluator(m =>
        {
            return $"{{{m.Groups[1].Value.Replace('-', '_')}}}";
        }));
        foreach (var param in parameters)
        {
            result = result.Replace($"{{{param.Key}}}", param.Value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dataLoader?.Dispose();
            _definitionLoader?.Dispose();
        }
        base.Dispose(disposing);
    }
}
