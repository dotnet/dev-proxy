// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using System.Security.Claims;

namespace Microsoft.DevProxy.Plugins.Mocks;

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

public class CrudApiEntraAuth
{
    public string Audience { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public string[] Roles { get; set; } = [];
    public bool ValidateLifetime { get; set; } = false;
    public bool ValidateSigningKey { get; set; } = false;
}

public class CrudApiAction
{
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiActionType Action { get; set; } = CrudApiActionType.GetAll;
    public string Url { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string Query { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiAuthType Auth { get; set; } = CrudApiAuthType.None;
    public CrudApiEntraAuth? EntraAuthConfig { get; set; }
}

public class CrudApiConfiguration
{
    public string ApiFile { get; set; } = "api.json";
    public string BaseUrl { get; set; } = string.Empty;
    public string DataFile { get; set; } = string.Empty;
    public IEnumerable<CrudApiAction> Actions { get; set; } = [];
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public CrudApiAuthType Auth { get; set; } = CrudApiAuthType.None;
    public CrudApiEntraAuth? EntraAuthConfig { get; set; }
}

public class CrudApiPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    protected CrudApiConfiguration _configuration = new();
    private CrudApiDefinitionLoader? _loader = null;
    public override string Name => nameof(CrudApiPlugin);
    private IProxyConfiguration? _proxyConfiguration;
    private JArray? _data;
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.BeforeRequest += OnRequestAsync;

        _proxyConfiguration = Context.Configuration;

        _configuration.ApiFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.ApiFile), Path.GetDirectoryName(_proxyConfiguration?.ConfigFile ?? string.Empty) ?? string.Empty);

        _loader = new CrudApiDefinitionLoader(Logger, _configuration);
        _loader?.InitApiDefinitionWatcher();

        if (_configuration.Auth == CrudApiAuthType.Entra &&
            _configuration.EntraAuthConfig is null)
        {
            Logger.LogError("Entra auth is enabled but no configuration is provided. API will work anonymously.");
            _configuration.Auth = CrudApiAuthType.None;
        }

        LoadData();
        await SetupOpenIdConnectConfigurationAsync();
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
            var dataFilePath = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.DataFile), Path.GetDirectoryName(_configuration.ApiFile) ?? string.Empty);
            if (!File.Exists(dataFilePath))
            {
                Logger.LogError($"Data file '{dataFilePath}' does not exist. The {_configuration.BaseUrl} API will be disabled.");
                _configuration.Actions = [];
                return;
            }

            var dataString = File.ReadAllText(dataFilePath);
            _data = JArray.Parse(dataString);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while reading {configFile}", _configuration.DataFile);
        }
    }

    protected virtual Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        Request request = e.Session.HttpClient.Request;
        ResponseState state = e.ResponseState;

        if (UrlsToWatch is null ||
            !e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
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
            Logger.LogRequest("Did not match any action", MessageType.Skipped, new LoggingContext(e.Session));
        }

        return Task.CompletedTask;
    }

    private bool AuthorizeRequest(ProxyRequestArgs e, CrudApiAction? action = null)
    {
        var authType = action is null ? _configuration.Auth : action.Auth;
        var authConfig = action is null ? _configuration.EntraAuthConfig : action.EntraAuthConfig;

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
            Logger.LogRequest("401 Unauthorized. No token found on the request.", MessageType.Failed, new LoggingContext(e.Session));
            return false;
        }

        // does the token has a valid format
        var tokenHeaderParts = token.Split(' ');
        if (tokenHeaderParts.Length != 2 || tokenHeaderParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, new LoggingContext(e.Session));
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
            if (authConfig.Roles.Length != 0)
            {
                var rolesFromTheToken = string.Join(' ', claimsPrincipal.Claims
                    .Where(c => c.Type == ClaimTypes.Role)
                    .Select(c => c.Value));

                if (!authConfig.Roles.Any(r => HasPermission(r, rolesFromTheToken)))
                {
                    var rolesRequired = string.Join(", ", authConfig.Roles);

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, new LoggingContext(e.Session));
                    return false;
                }

                return true;
            }
            if (authConfig.Scopes.Length != 0)
            {
                var scopesFromTheToken = string.Join(' ', claimsPrincipal.Claims
                    .Where(c => c.Type == "http://schemas.microsoft.com/identity/claims/scope")
                    .Select(c => c.Value));

                if (!authConfig.Scopes.Any(s => HasPermission(s, scopesFromTheToken)))
                {
                    var scopesRequired = string.Join(", ", authConfig.Scopes);

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, new LoggingContext(e.Session));
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, new LoggingContext(e.Session));
            return false;
        }

        return true;
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

    private static void SendUnauthorizedResponse(SessionEventArgs e)
    {
        SendJsonResponse("{\"error\":{\"message\":\"Unauthorized\"}}", HttpStatusCode.Unauthorized, e);
    }

    private static void SendNotFoundResponse(SessionEventArgs e)
    {
        SendJsonResponse("{\"error\":{\"message\":\"Not found\"}}", HttpStatusCode.NotFound, e);
    }

    private static string ReplaceParams(string query, IDictionary<string, string> parameters)
    {
        var result = Regex.Replace(query, "{([^}]+)}", new MatchEvaluator(m =>
        {
            return $"{{{m.Groups[1].Value.Replace('-', '_')}}}";
        }));
        foreach (var param in parameters)
        {
            result = result.Replace($"{{{param.Key}}}", param.Value);
        }
        return result;
    }

    private static void SendEmptyResponse(HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader>();
        if (e.HttpClient.Request.Headers.Any(h => h.Name == "Origin"))
        {
            headers.Add(new HttpHeader("access-control-allow-origin", "*"));
        }
        e.GenericResponse("", statusCode, headers);
    }

    private static void SendJsonResponse(string body, HttpStatusCode statusCode, SessionEventArgs e)
    {
        var headers = new List<HttpHeader> {
            new("content-type", "application/json; charset=utf-8")
        };
        if (e.HttpClient.Request.Headers.Any(h => h.Name == "Origin"))
        {
            headers.Add(new HttpHeader("access-control-allow-origin", "*"));
        }
        e.GenericResponse(body, statusCode, headers);
    }

    private void GetAll(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        SendJsonResponse(JsonConvert.SerializeObject(_data, Formatting.Indented), HttpStatusCode.OK, e);
        Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e));
    }

    private void GetOne(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                SendNotFoundResponse(e);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e));
                return;
            }

            SendJsonResponse(JsonConvert.SerializeObject(item, Formatting.Indented), HttpStatusCode.OK, e);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
        }
    }

    private void GetMany(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            var items = (_data?.SelectTokens(ReplaceParams(action.Query, parameters))) ?? [];
            SendJsonResponse(JsonConvert.SerializeObject(items, Formatting.Indented), HttpStatusCode.OK, e);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
        }
    }

    private void Create(SessionEventArgs e, CrudApiAction action, IDictionary<string, string> parameters)
    {
        try
        {
            _data?.Add(JObject.Parse(e.HttpClient.Request.BodyString));
            SendJsonResponse(JsonConvert.SerializeObject(e.HttpClient.Request.BodyString, Formatting.Indented), HttpStatusCode.Created, e);
            Logger.LogRequest($"201 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
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
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item)?.Merge(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
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
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e));
                return;
            }
            var update = JObject.Parse(e.HttpClient.Request.BodyString);
            ((JContainer)item)?.Replace(update);
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
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
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, new LoggingContext(e));
                return;
            }

            item?.Remove();
            SendEmptyResponse(HttpStatusCode.NoContent, e);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, new LoggingContext(e));
        }
        catch (Exception ex)
        {
            SendJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, e);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, new LoggingContext(e));
        }
    }

    private (Action<SessionEventArgs, CrudApiAction, IDictionary<string, string>> handler, CrudApiAction action, IDictionary<string, string> parameters)? GetMatchingActionHandler(Request request)
    {
        if (_configuration.Actions is null ||
            !_configuration.Actions.Any())
        {
            return null;
        }

        var parameterMatchEvaluator = new MatchEvaluator(m =>
        {
            var paramName = m.Value.Trim('{', '}').Replace('-', '_');
            return $"(?<{paramName}>[^/&]+)";
        });

        var parameters = new Dictionary<string, string>();
        var action = _configuration.Actions.FirstOrDefault(action =>
        {
            if (action.Method != request.Method) return false;
            var absoluteActionUrl = (_configuration.BaseUrl + action.Url).Replace("//", "/", 8);

            if (absoluteActionUrl == request.Url)
            {
                return true;
            }

            // check if the action contains parameters
            // if it doesn't, it's not a match for the current request for sure
            if (!absoluteActionUrl.Contains('{'))
            {
                return false;
            }

            // convert parameters into named regex groups
            var urlRegex = Regex.Replace(Regex.Escape(absoluteActionUrl).Replace("\\{", "{"), "({[^}]+})", parameterMatchEvaluator);
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
}
