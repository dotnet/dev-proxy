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
using System.Text;
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
    private CrudApiDefinitionLoader? _loader;
    private JArray? _data;
    private OpenIdConnectConfiguration? _openIdConnectConfiguration;

    public override string Name => nameof(CrudApiPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        Configuration.ApiFile = ProxyUtils.GetFullPath(Configuration.ApiFile, ProxyConfiguration.ConfigFile);

        _loader = ActivatorUtilities.CreateInstance<CrudApiDefinitionLoader>(e.ServiceProvider, Configuration);
        await _loader.InitFileWatcherAsync(cancellationToken);

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

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (IsCORSPreflightRequest(args.Request) && Configuration.EnableCORS)
        {
            var corsResponse = BuildEmptyResponse(HttpStatusCode.NoContent, args.Request);
            Logger.LogRequest("CORS preflight request", MessageType.Mocked, args.Request);
            return PluginResponse.Respond(corsResponse);
        }

        if (!AuthorizeRequest(args.Request))
        {
            return PluginResponse.Respond(BuildUnauthorizedResponse(args.Request));
        }

        var actionAndParams = GetMatchingActionHandler(args.Request);
        if (actionAndParams is not null)
        {
            if (!AuthorizeRequest(args.Request, actionAndParams.Value.action))
            {
                return PluginResponse.Respond(BuildUnauthorizedResponse(args.Request));
            }

            var response = await actionAndParams.Value.handler(args.Request, actionAndParams.Value.action, actionAndParams.Value.parameters, cancellationToken);
            return PluginResponse.Respond(response);
        }
        else
        {
            Logger.LogRequest("Did not match any action", MessageType.Skipped, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return PluginResponse.Continue();
    };

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

    private (Func<HttpRequestMessage, CrudApiAction, IDictionary<string, string>, CancellationToken, Task<HttpResponseMessage>> handler, CrudApiAction action, IDictionary<string, string> parameters)? GetMatchingActionHandler(HttpRequestMessage request)
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
            if (action.Method != request.Method.Method)
            {
                return false;
            }

            var absoluteActionUrl = (Configuration.BaseUrl + action.Url).Replace("//", "/", 8);

            if (absoluteActionUrl == request.RequestUri?.ToString())
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
            var match = Regex.Match(request.RequestUri?.ToString() ?? string.Empty, urlRegex);
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
            CrudApiActionType.Create => CreateAsync,
            CrudApiActionType.GetAll => GetAllAsync,
            CrudApiActionType.GetOne => GetOneAsync,
            CrudApiActionType.GetMany => GetManyAsync,
            CrudApiActionType.Merge => MergeAsync,
            CrudApiActionType.Update => UpdateAsync,
            CrudApiActionType.Delete => DeleteAsync,
            _ => throw new NotImplementedException()
        }, action, parameters);
    }

    private void AddCORSHeaders(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (!request.Headers.TryGetValues("Origin", out var originValues))
        {
            return;
        }

        var origin = originValues.FirstOrDefault();
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        _ = response.Headers.TryAddWithoutValidation("access-control-allow-origin", origin);

        if (Configuration.EntraAuthConfig is not null ||
            Configuration.Actions.Any(a => a.Auth == CrudApiAuthType.Entra))
        {
            _ = response.Headers.TryAddWithoutValidation("access-control-allow-headers", "authorization, content-type");
        }

        var methods = string.Join(", ", Configuration.Actions
            .Where(a => a.Method is not null)
            .Select(a => a.Method)
            .Distinct());

        _ = response.Headers.TryAddWithoutValidation("access-control-allow-methods", methods);
    }

    private bool AuthorizeRequest(HttpRequestMessage request, CrudApiAction? action = null)
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

        var authHeaderValue = request.Headers.Authorization?.ToString();
        // is there a token
        if (string.IsNullOrEmpty(authHeaderValue))
        {
            Logger.LogRequest("401 Unauthorized. No token found on the request.", MessageType.Failed, request);
            return false;
        }

        // does the token has a valid format
        var tokenHeaderParts = authHeaderValue.Split(' ');
        if (tokenHeaderParts.Length != 2 || tokenHeaderParts[0] != "Bearer")
        {
            Logger.LogRequest("401 Unauthorized. The specified token is not a valid Bearer token.", MessageType.Failed, request);
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

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary role(s). Required one of: {rolesRequired}, found: {rolesFromTheToken}", MessageType.Failed, request);
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

                    Logger.LogRequest($"401 Unauthorized. The specified token does not have the necessary scope(s). Required one of: {scopesRequired}, found: {scopesFromTheToken}", MessageType.Failed, request);
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogRequest($"401 Unauthorized. The specified token is not valid: {ex.Message}", MessageType.Failed, request);
            return false;
        }

        return true;
    }

    private HttpResponseMessage BuildUnauthorizedResponse(HttpRequestMessage request)
    {
        var body = new
        {
            error = new
            {
                message = "Unauthorized"
            }
        };
        return BuildJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.Unauthorized, request);
    }

    private HttpResponseMessage BuildNotFoundResponse(HttpRequestMessage request)
    {
        var body = new
        {
            error = new
            {
                message = "Not found"
            }
        };
        return BuildJsonResponse(System.Text.Json.JsonSerializer.Serialize(body, ProxyUtils.JsonSerializerOptions), HttpStatusCode.NotFound, request);
    }

    private HttpResponseMessage BuildEmptyResponse(HttpStatusCode statusCode, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
        };
        AddCORSHeaders(request, response);
        return response;
    }

    private HttpResponseMessage BuildJsonResponse(string body, HttpStatusCode statusCode, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddCORSHeaders(request, response);
        return response;
    }

    private Task<HttpResponseMessage> GetAllAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var response = BuildJsonResponse(JsonConvert.SerializeObject(_data, Formatting.Indented), HttpStatusCode.OK, request);
        Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, request);
        return Task.FromResult(response);
    }

    private Task<HttpResponseMessage> GetOneAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                var response = BuildNotFoundResponse(request);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, request);
                return Task.FromResult(response);
            }

            var successResponse = BuildJsonResponse(JsonConvert.SerializeObject(item, Formatting.Indented), HttpStatusCode.OK, request);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, request);
            return Task.FromResult(successResponse);
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return Task.FromResult(response);
        }
    }

    private Task<HttpResponseMessage> GetManyAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var items = (_data?.SelectTokens(ReplaceParams(action.Query, parameters))) ?? [];
            var response = BuildJsonResponse(JsonConvert.SerializeObject(items, Formatting.Indented), HttpStatusCode.OK, request);
            Logger.LogRequest($"200 {action.Url}", MessageType.Mocked, request);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return Task.FromResult(response);
        }
    }

    private async Task<HttpResponseMessage> CreateAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var bodyString = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            var data = JObject.Parse(bodyString);
            _data?.Add(data);
            var response = BuildJsonResponse(JsonConvert.SerializeObject(data, Formatting.Indented), HttpStatusCode.Created, request);
            Logger.LogRequest($"201 {action.Url}", MessageType.Mocked, request);
            return response;
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return response;
        }
    }

    private async Task<HttpResponseMessage> MergeAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                var response = BuildNotFoundResponse(request);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, request);
                return response;
            }
            var bodyString = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            var update = JObject.Parse(bodyString);
            ((JContainer)item).Merge(update);
            var successResponse = BuildEmptyResponse(HttpStatusCode.NoContent, request);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, request);
            return successResponse;
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return response;
        }
    }

    private async Task<HttpResponseMessage> UpdateAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                var response = BuildNotFoundResponse(request);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, request);
                return response;
            }
            var bodyString = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            var update = JObject.Parse(bodyString);
            ((JContainer)item).Replace(update);
            var successResponse = BuildEmptyResponse(HttpStatusCode.NoContent, request);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, request);
            return successResponse;
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return response;
        }
    }

    private Task<HttpResponseMessage> DeleteAsync(HttpRequestMessage request, CrudApiAction action, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            var item = _data?.SelectToken(ReplaceParams(action.Query, parameters));
            if (item is null)
            {
                var response = BuildNotFoundResponse(request);
                Logger.LogRequest($"404 {action.Url}", MessageType.Mocked, request);
                return Task.FromResult(response);
            }

            item.Remove();
            var successResponse = BuildEmptyResponse(HttpStatusCode.NoContent, request);
            Logger.LogRequest($"204 {action.Url}", MessageType.Mocked, request);
            return Task.FromResult(successResponse);
        }
        catch (Exception ex)
        {
            var response = BuildJsonResponse(JsonConvert.SerializeObject(ex, Formatting.Indented), HttpStatusCode.InternalServerError, request);
            Logger.LogRequest($"500 {action.Url}", MessageType.Failed, request);
            return Task.FromResult(response);
        }
    }

    private static bool IsCORSPreflightRequest(HttpRequestMessage request)
    {
        return request.Method == HttpMethod.Options &&
               request.Headers.TryGetValues("Origin", out var _);
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
