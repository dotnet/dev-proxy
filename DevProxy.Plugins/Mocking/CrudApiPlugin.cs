// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
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
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

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
    Entra
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
    ILogger<CrudApiPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<CrudApiConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private CrudApiDefinitionLoader? _loader;
    private JArray? _data;
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override string Name => nameof(CrudApiPlugin);

    public override async Task InitializeAsync(InitArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e);

        Configuration.ApiFile = ProxyUtils.GetFullPath(Configuration.ApiFile, ProxyConfiguration.ConfigFile);

        _loader = ActivatorUtilities.CreateInstance<CrudApiDefinitionLoader>(e.ServiceProvider, Configuration);
        _loader.InitFileWatcher();

        if (Configuration.Auth == CrudApiAuthType.Entra &&
            Configuration.EntraAuthConfig is null)
        {
            Logger.LogError("Entra auth is enabled but no configuration is provided. API will work anonymously.");
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

        LoadData();
        await SetupOpenIdConnectConfigurationAsync();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.Session.HttpClient.Request;
        var state = e.ResponseState;

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

        if (IsCORSPreflightRequest(request) && Configuration.EnableCORS)
        {
            SendEmptyResponse(HttpStatusCode.NoContent, e.Session);
            Logger.LogRequest("CORS preflight request", MessageType.Mocked, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (!AuthorizeRequest(e))
        {
            SendUnauthorizedResponse(e.Session);
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }

        var actionAndParams = GetMatchingActionHandler(request);
        if (actionAndParams is not null)
        {
            if (!AuthorizeRequest(e, actionAndParams.Value.action))
            {
                SendUnauthorizedResponse(e.Session);
                state.HasBeenSet = true;
                return Task.CompletedTask;
            }

            actionAndParams.Value.handler(e.Session, actionAndParams.Value.action, actionAndParams.Value.parameters);
            state.HasBeenSet = true;
        }
        else
        {
            Logger.LogRequest("Did not match any action", MessageType.Skipped, new(e.Session));
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

    private void LoadData()
    {
        try
        {
            var dataFilePath = Path.GetFullPath(ProxyUtils.ReplacePathTokens(Configuration.DataFile), Path.GetDirectoryName(Configuration.ApiFile) ?? string.Empty);
            if (!File.Exists(dataFilePath))
            {
                Logger.LogError("Data file '{DataFilePath}' does not exist. The {APIUrl} API will be disabled.", dataFilePath, Configuration.BaseUrl);
                Configuration.Actions = [];
                return;
            }

            var dataString = File.ReadAllText(dataFilePath);
            _data = JArray.Parse(dataString);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {ConfigFile}", Configuration.DataFile);
        }
    }

    private (Action<SessionEventArgs, CrudApiAction, IDictionary<string, string>> handler, CrudApiAction action, IDictionary<string, string> parameters)? GetMatchingActionHandler(Request request)
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

        var parameters = new Dictionary<string, string>();
        var action = Configuration.Actions.FirstOrDefault(action =>
        {
            if (action.Method != request.Method)
            {
                return false;
            }

            var absoluteActionUrl = (Configuration.BaseUrl + action.Url).Replace("//", "/", 8);

            if (absoluteActionUrl == request.Url)
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
            var match = Regex.Match(request.Url, urlRegex);
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

    private void AddCORSHeaders(Request request, List<HttpHeader> headers)
    {
        var origin = request.Headers.FirstOrDefault(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        headers.Add(new HttpHeader("access-control-allow-origin", origin));

        if (Configuration.EntraAuthConfig is not null ||
            Configuration.Actions.Any(a => a.Auth == CrudApiAuthType.Entra))
        {
            headers.Add(new HttpHeader("access-control-allow-headers", "authorization, content-type"));
        }

        var methods = string.Join(", ", Configuration.Actions
            .Where(a => a.Method is not null)
            .Select(a => a.Method)
            .Distinct());

        headers.Add(new HttpHeader("access-control-allow-methods", methods));
    }

    private bool AuthorizeRequest(ProxyRequestArgs e, CrudApiAction? action = null)
    {
        var authType = action is null ? Configuration.Auth : action.Auth;
        var authConfig = action is null ? Configuration.EntraAuthConfig : action.EntraAuthConfig;

        if (authType == CrudApiAuthType.None)
        {
            if (action is null)
            {
                Logger.LogDebug("No auth is required for this API.");
            }
            return true;
        }

        Debug.Assert(authConfig is not null, "EntraAuthConfig is null when auth is required.");

        var token = e.Session.HttpClient.Request.Headers.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))?.Value;
        // is there a token
        if (string.IsNullOrEmpty(token))
        {
            Logger.LogRequest("401 Unauthorized. No token found on the request.", MessageType.Failed, new(e.Session));
            return false;
        }

        // does the token has a valid format
        var tokenHeaderParts = token.Split(' ');
        if (tokenHeaderParts.Length != 2 || tokenHeaderParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, new(e.Session));
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

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, new(e.Session));
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

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, new(e.Session));
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, new(e.Session));
            return false;
        }

        return true;
    }

    private void SendUnauthorizedResponse(SessionEventArgs e)
    {
        var body = new
        {
            error = new
            {
                message = "Unauthorized"
            }
        };
        SendJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.Unauthorized, e);
    }

    private void SendNotFoundResponse(SessionEventArgs e)
    {
        var body = new
        {
            error = new
            {
                message = "Not found"
            }
        };
        SendJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.NotFound, e);
    }

    private void SendEmptyResponse(HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader>();
        AddCORSHeaders(e.HttpClient.Request, headers);
        e.GenericResponse("", statusCode, headers);
    }

    private void SendJsonResponse(string body, HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader> {
            new("content-type", "application/json; charset=utf-8")
        };
        AddCORSHeaders(e.HttpClient.Request, headers);
        e.GenericResponse(body, statusCode, headers);
    }

    private void GetAll(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        SendJsonResponse(JsonConvert.SerializeObject(_data, Formatting.Indented), HttpStatusCode.OK, e);
        Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new(e));
    }

    private void GetOne(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new(e));
                return;
            }

            SendJsonResponse(JsonConvert.SerializeObject(item, Formatting.Indented), HttpStatusCode.OK, e);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private void GetMany(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var items = (_data?.SelectTokens(ReplaceParams(action.Query, parameters))) ?? [];
            SendJsonResponse(JsonConvert.SerializeObject(items, Formatting.Indented), HttpStatusCode.OK, e);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private void Create(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var data = JObject.Parse(e.HttpClient.Request.BodyString);
            _data?.Add(data);
            SendJsonResponse(JsonConvert.SerializeObject(data, Formatting.Indented), HttpStatusCode.Created, e);
            Logger.LogRequest($"201 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private void Merge(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item).Merge(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private void Update(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item).Replace(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private void Delete(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new(e));
                return;
            }

            item.Remove();
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new(e));
        }
    }

    private static bool IsCORSPreflightRequest(Request request)
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
}
