// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models.TypeSpec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Titanium.Web.Proxy.Http;

namespace DevProxy.Plugins.Generation;

public sealed class TypeSpecGeneratorPluginReportItem
{
    public required string FileName { get; init; }
    public required string ServerUrl { get; init; }
}

public sealed class TypeSpecGeneratorPluginReport : List<TypeSpecGeneratorPluginReportItem>
{
    public TypeSpecGeneratorPluginReport() : base() { }

    public TypeSpecGeneratorPluginReport(IEnumerable<TypeSpecGeneratorPluginReportItem> collection) : base(collection) { }
}

public sealed class TypeSpecGeneratorPluginConfiguration
{
    public bool IgnoreResponseTypes { get; set; }
}

public sealed class TypeSpecGeneratorPlugin(
    ILogger<TypeSpecGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<TypeSpecGeneratorPluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public static readonly string GeneratedTypeSpecFilesKey = "GeneratedTypeSpecFiles";

    public override string Name => nameof(TypeSpecGeneratorPlugin);

    public override async Task AfterRecordingStopAsync(RecordingArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Creating TypeSpec files from recorded requests...");

        var typeSpecFiles = new List<TypeSpecFile>();

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null ||
              request.Url is null ||
              request.Method is null ||
              // TypeSpec does not support OPTIONS requests
              string.Equals(request.Context.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) ||
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {MethodAndUrlString}...", methodAndUrlString);

            var url = new Uri(request.Url);
            var doc = await GetOrCreateTypeSpecFileAsync(typeSpecFiles, url);

            var serverUrl = url.GetLeftPart(UriPartial.Authority);
            if (!doc.Service.Servers.Any(x => x.Url.Equals(serverUrl, StringComparison.OrdinalIgnoreCase)))
            {
                doc.Service.Servers.Add(new()
                {
                    Url = serverUrl
                });
            }

            var op = await GetOperationAsync(request, doc);

            doc.Service.Namespace.MergeOperation(op);
        }

        Logger.LogDebug("Serializing TypeSpec files...");
        var generatedTypeSpecFiles = new Dictionary<string, string>();
        foreach (var typeSpecFile in typeSpecFiles)
        {
            var fileName = $"{typeSpecFile.Name}-{DateTime.Now:yyyyMMddHHmmss}.tsp";
            Logger.LogDebug("Writing OpenAPI spec to {FileName}...", fileName);
            await File.WriteAllTextAsync(fileName, typeSpecFile.ToString());

            generatedTypeSpecFiles.Add(typeSpecFile.Service.Servers.First().Url, fileName);

            Logger.LogInformation("Created OpenAPI spec file {FileName}", fileName);
        }

        StoreReport(new TypeSpecGeneratorPluginReport(
            generatedTypeSpecFiles
            .Select(kvp => new TypeSpecGeneratorPluginReportItem
            {
                ServerUrl = kvp.Key,
                FileName = kvp.Value
            })), e);

        // store the generated TypeSpec files in the global data
        // for use by other plugins
        e.GlobalData[GeneratedTypeSpecFilesKey] = generatedTypeSpecFiles;

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    private async Task<Operation> GetOperationAsync(RequestLog request, TypeSpecFile doc)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetOperationAsync));

        Debug.Assert(request.Context is not null, "request.Context is null");
        Debug.Assert(request.Method is not null, "request.Method is null");
        Debug.Assert(request.Url is not null, "request.Url is null");

        var url = new Uri(request.Url);
        var httpRequest = request.Context.Session.HttpClient.Request;
        var httpResponse = request.Context.Session.HttpClient.Response;

        var (route, parameters) = await GetRouteAndParametersAsync(url);
        var op = new Operation
        {
            Name = await GetOperationNameAsync(request.Method, url),
            Description = await GetOperationDescriptionAsync(request.Method, url),
            Method = Enum.Parse<HttpVerb>(request.Method, true),
            Route = route,
            Doc = doc
        };
        op.Parameters.AddRange(parameters);

        var lastSegment = GetLastNonParametrizableSegment(url);
        await ProcessRequestBodyAsync(httpRequest, doc, op, lastSegment);
        ProcessRequestHeaders(httpRequest, op);
        ProcessAuth(httpRequest, doc, op);
        await ProcessResponseAsync(httpResponse, doc, op, lastSegment, url);

        Logger.LogTrace("Left {Name}", nameof(GetOperationAsync));

        return op;
    }

    private void ProcessAuth(Request httpRequest, TypeSpecFile doc, Operation op)
    {
        Logger.LogTrace("Entered {Name}", nameof(ProcessAuth));

        var authHeaders = httpRequest.Headers
            .Where(h => Http.AuthHeaders.Contains(h.Name.ToLowerInvariant()))
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
                AddApiKeyAuth(op, name, ApiKeyLocation.Header);
                return;
            }

            var bearerToken = value["Bearer ".Length..].Trim();
            AddAuthorizationAuth(op, bearerToken, doc);
            return;
        }

        var query = HttpUtility.ParseQueryString(httpRequest.RequestUri.Query);
        var authQueryParam = query.AllKeys
            .FirstOrDefault(k => k is not null && Http.AuthHeaders.Contains(k.ToLowerInvariant()));
        if (authQueryParam is not null)
        {
            Logger.LogDebug("Found auth query parameter: {AuthQueryParam}", authQueryParam);
            AddApiKeyAuth(op, authQueryParam, ApiKeyLocation.Query);
        }
        else
        {
            Logger.LogDebug("No auth headers or query parameters found");
        }

        Logger.LogTrace("Left {Name}", nameof(ProcessAuth));
    }

    private void AddAuthorizationAuth(Operation op, string bearerToken, TypeSpecFile doc)
    {
        Logger.LogTrace("Entered {Name}", nameof(AddAuthorizationAuth));

        if (IsJwtToken(bearerToken, out var jwtToken))
        {
            var issuer = jwtToken.Issuer;
            Logger.LogDebug("Issuer: {Issuer}", issuer);
            var scopes = jwtToken.Claims
                .Where(c => c.Type == "scp")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct()
                .ToList();
            Logger.LogDebug("Scopes: {Scopes}", string.Join(", ", scopes));
            var roles = jwtToken.Claims
                .Where(c => c.Type == "roles")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct();
            Logger.LogDebug("Roles: {Roles}", string.Join(", ", roles));
            scopes.AddRange(roles);

            OAuth2Auth? auth = null;
            if (IsEntraToken(issuer))
            {
                var version = jwtToken.Claims
                    .FirstOrDefault(c => c.Type == "ver")?.Value ?? "1.0";
                var baseUrl = issuer.Contains("v2.0", StringComparison.OrdinalIgnoreCase) ?
                    issuer.Replace("v2.0", "", StringComparison.OrdinalIgnoreCase) : issuer;
                auth = new()
                {
                    Name = $"EntraOAuth2Auth",
                    FlowType = roles.Any() ? FlowType.ClientCredentials : FlowType.AuthorizationCode,
                    TokenUrl = version == "1.0" ? $"{baseUrl}oauth2/token" : $"{baseUrl}oauth2/v2.0/token",
                    AuthorizationUrl = version == "1.0" ? $"{baseUrl}oauth2/authorize" : $"{baseUrl}oauth2/v2.0/authorize",
                    RefreshUrl = version == "1.0" ? $"{baseUrl}oauth2/token" : $"{baseUrl}oauth2/v2.0/token",
                    Scopes = [.. scopes]
                };
            }
            else
            {
                auth = new()
                {
                    Name = $"APIOAuth2Auth",
                    FlowType = FlowType.AuthorizationCode,
                    TokenUrl = jwtToken.Issuer,
                    AuthorizationUrl = jwtToken.Issuer,
                    Scopes = [.. scopes]
                };
            }
            doc.Service.Namespace.Auth = auth;
            op.Auth = auth;
        }
        else
        {
            op.Auth = new BearerAuth();
        }

        Logger.LogTrace("Left {Name}", nameof(AddAuthorizationAuth));
    }

    private bool IsEntraToken(string issuer)
    {
        Logger.LogTrace("Entered {Name}", nameof(IsEntraToken));

        var isEntraToken = issuer.Contains("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase) ||
            issuer.Contains("https://sts.windows.net/", StringComparison.OrdinalIgnoreCase);

        Logger.LogDebug("Is token from Entra? {IsEntraToken}", isEntraToken);
        Logger.LogTrace("Left {Name}", nameof(IsEntraToken));

        return isEntraToken;
    }

    private void AddApiKeyAuth(Operation op, string name, ApiKeyLocation location)
    {
        Logger.LogTrace("Entered {Name}", nameof(AddApiKeyAuth));

        var apiKeyAuth = new ApiKeyAuth
        {
            Name = name,
            In = location
        };
        op.Auth = apiKeyAuth;

        Logger.LogTrace("Left {Name}", nameof(AddApiKeyAuth));
    }

    private bool IsJwtToken(string bearerToken, out JwtSecurityToken jwtToken)
    {
        Logger.LogTrace("Entered {Name}", nameof(IsJwtToken));

        jwtToken = new();

        try
        {
            jwtToken = new(bearerToken);
            Logger.LogTrace("Left {Name}", nameof(IsJwtToken));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Failed to parse JWT token: {Ex}", ex.Message);
        }

        Logger.LogTrace("Left {Name}", nameof(IsJwtToken));
        return false;
    }

    private async Task ProcessRequestBodyAsync(Request httpRequest, TypeSpecFile doc, Operation op, string lastSegment)
    {
        Logger.LogTrace("Entered {Name}", nameof(ProcessRequestBodyAsync));

        if (!httpRequest.HasBody)
        {
            Logger.LogDebug("Request has no body, skipping...");
            return;
        }

        var models = await GetModelsFromStringAsync(httpRequest.BodyString, lastSegment.ToPascalCase());
        if (models.Length > 0)
        {
            foreach (var model in models)
            {
                _ = doc.Service.Namespace.MergeModel(model);
            }

            var rootModel = models.Last();
            op.Parameters.Add(new()
            {
                Name = await GetParameterNameAsync(rootModel),
                Value = rootModel.Name,
                In = ParameterLocation.Body
            });
        }

        Logger.LogTrace("Left {Name}", nameof(ProcessRequestBodyAsync));
    }

    private void ProcessRequestHeaders(Request httpRequest, Operation op)
    {
        Logger.LogTrace("Entered {Name}", nameof(ProcessRequestHeaders));

        foreach (var header in httpRequest.Headers)
        {
            if (Http.StandardHeaders.Contains(header.Name.ToLowerInvariant()) ||
                Http.AuthHeaders.Contains(header.Name.ToLowerInvariant()))
            {
                continue;
            }

            op.Parameters.Add(new()
            {
                Name = header.Name,
                Value = GetValueType(header.Value),
                In = ParameterLocation.Header
            });
        }

        Logger.LogTrace("Left {Name}", nameof(ProcessRequestHeaders));
    }

    private async Task ProcessResponseAsync(Response? httpResponse, TypeSpecFile doc, Operation op, string lastSegment, Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(ProcessResponseAsync));

        if (httpResponse is null)
        {
            Logger.LogDebug("Response is null, skipping...");
            return;
        }

        OperationResponseModel res;

        if (Configuration.IgnoreResponseTypes)
        {
            res = new()
            {
                StatusCode = httpResponse.StatusCode,
                BodyType = "string"
            };
        }
        else
        {
            res = new()
            {
                StatusCode = httpResponse.StatusCode,
                Headers = httpResponse.Headers
                    .Where(h => !Http.StandardHeaders.Contains(h.Name.ToLowerInvariant()) &&
                                !Http.AuthHeaders.Contains(h.Name.ToLowerInvariant()))
                    .ToDictionary(h => h.Name.ToCamelCase(), h => h.Value.GetType().Name)
            };

            var models = await GetModelsFromStringAsync(httpResponse.BodyString, lastSegment.ToPascalCase(), httpResponse.StatusCode >= 400);
            if (models.Length > 0)
            {
                foreach (var model in models)
                {
                    _ = doc.Service.Namespace.MergeModel(model);
                }

                var rootModel = models.Last();
                if (rootModel.IsArray)
                {
                    res.BodyType = $"{rootModel.Name}[]";
                    op.Name = await GetOperationNameAsync("list", url);
                }
                else
                {
                    res.BodyType = rootModel.Name;
                }
            }
        }

        op.MergeResponse(res);

        Logger.LogTrace("Left {Name}", nameof(ProcessResponseAsync));
    }

    private async Task<string> GetParameterNameAsync(Model model)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetParameterNameAsync));

        var name = model.IsArray ? SanitizeName(await MakeSingularAsync(model.Name)) : model.Name;
        if (string.IsNullOrEmpty(name))
        {
            name = model.Name;
        }

        Logger.LogDebug("Parameter name: {Name}", name);
        Logger.LogTrace("Left {Name}", nameof(GetParameterNameAsync));

        return name;
    }

    private async Task<TypeSpecFile> GetOrCreateTypeSpecFileAsync(List<TypeSpecFile> files, Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetOrCreateTypeSpecFileAsync));

        var name = GetName(url);
        Logger.LogDebug("Name: {Name}", name);
        var file = files.FirstOrDefault(d => d.Name == name);
        if (file is null)
        {
            Logger.LogDebug("Creating new TypeSpec file: {Name}", name);

            var serviceTitle = await GetServiceTitleAsync(url);
            file = new()
            {
                Name = name,
                Service = new()
                {
                    Title = serviceTitle,
                    Namespace = new()
                    {
                        Name = GetRootNamespaceName(url)
                    }
                }
            };
            files.Add(file);
        }
        else
        {
            Logger.LogDebug("Using existing TypeSpec file: {Name}", name);
        }

        Logger.LogTrace("Left {Name}", nameof(GetOrCreateTypeSpecFileAsync));

        return file;
    }

    private string GetRootNamespaceName(Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetRootNamespaceName));

        var ns = SanitizeName(string.Join("", url.Host.Split('.').Select(x => x.ToPascalCase())));
        if (string.IsNullOrEmpty(ns))
        {
            ns = GetRandomName();
        }

        Logger.LogDebug("Root namespace name: {Ns}", ns);
        Logger.LogTrace("Left {Name}", nameof(GetRootNamespaceName));

        return ns;
    }

    private async Task<string> GetOperationNameAsync(string method, Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetOperationNameAsync));

        var lastSegment = GetLastNonParametrizableSegment(url);
        Logger.LogDebug("Url: {Url}", url);
        Logger.LogDebug("Last non-parametrizable segment: {LastSegment}", lastSegment);

        var name = method == "list" ? lastSegment : await MakeSingularAsync(lastSegment);
        if (string.IsNullOrEmpty(name))
        {
            name = lastSegment;
        }
        name = SanitizeName(name);
        if (string.IsNullOrEmpty(name))
        {
            name = SanitizeName(lastSegment);
            if (string.IsNullOrEmpty(name))
            {
                name = GetRandomName();
            }
        }

        var operationName = $"{method.ToLowerInvariant()}{name.ToPascalCase()}";
        var sanitizedName = SanitizeName(operationName);
        if (!string.IsNullOrEmpty(sanitizedName))
        {
            Logger.LogDebug("Sanitized operation name: {SanitizedName}", sanitizedName);
            operationName = sanitizedName;
        }

        Logger.LogDebug("Operation name: {OperationName}", operationName);
        Logger.LogTrace("Left {Name}", nameof(GetOperationNameAsync));

        return operationName;
    }

    private string GetRandomName()
    {
        Logger.LogTrace("Entered {Name}", nameof(GetRandomName));

        var name = Guid.NewGuid().ToString("N");

        Logger.LogDebug("Random name: {Name}", name);
        Logger.LogTrace("Left {Name}", nameof(GetRandomName));

        return name;
    }

    private async Task<string> GetOperationDescriptionAsync(string method, Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetOperationDescriptionAsync));

        var prompt = $"You're an expert in OpenAPI. You help developers build great OpenAPI specs for use with LLMs. For the specified request, generate a one-sentence description. Respond with just the description. For example, for a request such as `GET https://api.contoso.com/books/{{books-id}}` you return `Get a book by ID`. Request: {method.ToUpperInvariant()} {url}";
        ILanguageModelCompletionResponse? description = null;
        if (await languageModelClient.IsEnabledAsync())
        {
            description = await languageModelClient.GenerateCompletionAsync(prompt);
        }

        var operationDescription = description?.Response ?? $"{method.ToUpperInvariant()} {url}";

        Logger.LogDebug("Operation description: {OperationDescription}", operationDescription);
        Logger.LogTrace("Left {Name}", nameof(GetOperationDescriptionAsync));

        return operationDescription;
    }

    private string GetName(Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetName));

        var name = url.Host.Replace(".", "-", StringComparison.OrdinalIgnoreCase).ToKebabCase();

        Logger.LogDebug("Name: {Name}", name);
        Logger.LogTrace("Left {Name}", nameof(GetName));

        return name;
    }

    private async Task<string> GetServiceTitleAsync(Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetServiceTitleAsync));

        var prompt = $"Based on the following host name, generate a descriptive name of an API service hosted on this URL. Respond with just the name. Host name: {url.Host}";
        ILanguageModelCompletionResponse? serviceTitle = null;
        if (await languageModelClient.IsEnabledAsync())
        {
            serviceTitle = await languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
        }
        var st = serviceTitle?.Response?.Trim('"') ?? $"{url.Host.Split('.').First().ToPascalCase()} API";

        Logger.LogDebug("Service title: {St}", st);
        Logger.LogTrace("Left {Name}", nameof(GetServiceTitleAsync));

        return st;
    }

    private string GetLastNonParametrizableSegment(Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetLastNonParametrizableSegment));

        var segments = url.Segments;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].Trim('/');
            if (!IsParametrizable(segment))
            {
                Logger.LogDebug("Last non-parametrizable segment: {Segment}", segment);
                Logger.LogTrace("Left {Name}", nameof(GetLastNonParametrizableSegment));

                return segment;
            }
        }

        Logger.LogDebug("No non-parametrizable segment found, returning empty string");
        Logger.LogTrace("Left {Name}", nameof(GetLastNonParametrizableSegment));

        return string.Empty;
    }

    private bool IsParametrizable(string segment)
    {
        Logger.LogTrace("Entered {Name}", nameof(IsParametrizable));

        var isParametrizable = Guid.TryParse(segment, out _) ||
          int.TryParse(segment, out _);

        Logger.LogDebug("Is segment '{Segment}' parametrizable? {IsParametrizable}", segment, isParametrizable);
        Logger.LogTrace("Left {Name}", nameof(IsParametrizable));

        return isParametrizable;
    }

    private async Task<(string Route, Parameter[] Parameters)> GetRouteAndParametersAsync(Uri url)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetRouteAndParametersAsync));

        var route = new List<string>();
        var parameters = new List<Parameter>();
        var previousSegment = "item";

        foreach (var segment in url.Segments)
        {
            Logger.LogDebug("Processing segment: {Segment}", segment);

            var segmentTrimmed = segment.Trim('/');
            if (string.IsNullOrEmpty(segmentTrimmed))
            {
                continue;
            }

            if (IsParametrizable(segmentTrimmed))
            {
                var paramName = $"{previousSegment}Id";
                parameters.Add(new()
                {
                    Name = paramName,
                    Value = GetValueType(segmentTrimmed),
                    In = ParameterLocation.Path
                });
                route.Add($"{{{paramName}}}");
            }
            else
            {
                previousSegment = SanitizeName(await MakeSingularAsync(segmentTrimmed));
                if (string.IsNullOrEmpty(previousSegment))
                {
                    previousSegment = SanitizeName(segmentTrimmed);
                    if (previousSegment.Length == 0)
                    {
                        previousSegment = GetRandomName();
                    }
                }
                previousSegment = previousSegment.ToCamelCase();
                route.Add(segmentTrimmed);
            }
        }

        if (url.Query.Length > 0)
        {
            Logger.LogDebug("Processing query string: {Query}", url.Query);

            var query = HttpUtility.ParseQueryString(url.Query);
            foreach (string key in query.Keys)
            {
                if (Http.AuthHeaders.Contains(key.ToLowerInvariant()))
                {
                    Logger.LogDebug("Skipping auth header: {Key}", key);
                    continue;
                }

                parameters.Add(new()
                {
                    Name = key.ToCamelFromKebabCase(),
                    Value = GetValueType(query[key]),
                    In = ParameterLocation.Query
                });
            }
        }
        else
        {
            Logger.LogDebug("No query string found in URL: {Url}", url);
        }

        Logger.LogTrace("Left {Name}", nameof(GetRouteAndParametersAsync));

        return (string.Join('/', route), parameters.ToArray());
    }

    private async Task<Model[]> GetModelsFromStringAsync(string? str, string name, bool isError = false)
    {
        Logger.LogTrace("Entered {Same}", nameof(GetModelsFromStringAsync));

        if (string.IsNullOrEmpty(str))
        {
            Logger.LogDebug("Empty string, returning empty model list");
            Logger.LogTrace("Left {Name}", nameof(GetModelsFromStringAsync));
            return [];
        }

        var models = new List<Model>();

        try
        {
            using var doc = JsonDocument.Parse(str, ProxyUtils.JsonDocumentOptions);
            var root = doc.RootElement;
            _ = await AddModelFromJsonElementAsync(root, name, isError, models);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Failed to parse JSON string, returning empty model list. Exception: {Ex}", ex.Message);

            // If the string is not a valid JSON, we return an empty model list
            Logger.LogTrace("Left {Name}", nameof(GetModelsFromStringAsync));
            return [];
        }

        Logger.LogTrace("Left {Name}", nameof(GetModelsFromStringAsync));

        return [.. models];
    }

    private async Task<string> AddModelFromJsonElementAsync(JsonElement jsonElement, string name, bool isError, List<Model> models)
    {
        Logger.LogTrace("Entered {Name}", nameof(AddModelFromJsonElementAsync));

#pragma warning disable IDE0010
        switch (jsonElement.ValueKind)
#pragma warning restore IDE0010
        {
            case JsonValueKind.String:
                return "string";
            case JsonValueKind.Number:
                return "numeric";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return "boolean";
            case JsonValueKind.Object:
                if (jsonElement.GetPropertyCount() == 0)
                {
                    models.Add(new()
                    {
                        Name = "Empty",
                        IsError = isError
                    });
                    return "Empty";
                }

                var model = new Model
                {
                    Name = await GetModelNameAsync(name),
                    IsError = isError
                };

                foreach (var p in jsonElement.EnumerateObject())
                {
                    var property = new ModelProperty
                    {
                        Name = p.Name,
                        Type = await AddModelFromJsonElementAsync(p.Value, p.Name.ToPascalCase(), isError, models)
                    };
                    model.Properties.Add(property);
                }
                models.Add(model);
                return model.Name;
            case JsonValueKind.Array:
                // we need to create a model for each item in the array
                // in case some items have null values or different shapes
                // we'll merge them later
                var modelName = string.Empty;
                foreach (var item in jsonElement.EnumerateArray())
                {
                    modelName = await AddModelFromJsonElementAsync(item, name, isError, models);
                }
                models.Add(new()
                {
                    Name = modelName,
                    IsError = isError,
                    IsArray = true
                });
                return $"{modelName}[]";
            case JsonValueKind.Null:
                return "null";
            default:
                return string.Empty;
        }
    }

    private async Task<string> GetModelNameAsync(string name)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetModelNameAsync));

        var modelName = SanitizeName(await MakeSingularAsync(name));
        if (string.IsNullOrEmpty(modelName))
        {
            modelName = SanitizeName(name);
            if (string.IsNullOrEmpty(modelName))
            {
                modelName = GetRandomName();
            }
        }

        modelName = modelName.ToPascalCase();

        Logger.LogDebug("Model name: {ModelName}", modelName);
        Logger.LogTrace("Left {Name}", nameof(GetModelNameAsync));

        return modelName;
    }

    private async Task<string> MakeSingularAsync(string noun)
    {
        Logger.LogTrace("Entered {Name}", nameof(MakeSingularAsync));

        var prompt = $"Make the following noun singular. Respond only with the word and nothing else. Don't translate. Word: {noun}";
        ILanguageModelCompletionResponse? singularNoun = null;
        if (await languageModelClient.IsEnabledAsync())
        {
            singularNoun = await languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
        }
        var singular = singularNoun?.Response;

        if (string.IsNullOrEmpty(singular) ||
            singular.Contains(' ', StringComparison.OrdinalIgnoreCase))
        {
            if (noun.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            {
                singular = noun[0..^3] + 'y';
            }
            else if (noun.EndsWith("es", StringComparison.OrdinalIgnoreCase))
            {
                singular = noun[0..^2];
            }
            else if (noun.EndsWith('s') && !noun.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            {
                singular = noun[0..^1];
            }
            else
            {
                singular = noun;
            }

            Logger.LogDebug("Failed to get singular form of {Noun} from LLM. Using fallback: {Singular}", noun, singular);
        }

        Logger.LogDebug("Singular form of '{Noun}': {Singular}", noun, singular);
        Logger.LogTrace("Left {Name}", nameof(MakeSingularAsync));

        return singular;
    }

    private string SanitizeName(string name)
    {
        Logger.LogTrace("Entered {Name}", nameof(SanitizeName));

        var sanitized = Regex.Replace(name, "[^a-zA-Z0-9_]", "");

        Logger.LogDebug("Sanitized name: {Name} to: {Sanitized}", name, sanitized);
        Logger.LogTrace("Left {Name}", nameof(SanitizeName));

        return sanitized;
    }

    private string GetValueType(string? value)
    {
        Logger.LogTrace("Entered {Name}", nameof(GetValueType));

        if (string.IsNullOrEmpty(value))
        {
            return "null";
        }
        else if (int.TryParse(value, out _))
        {
            return "integer";
        }
        else if (bool.TryParse(value, out _))
        {
            return "boolean";
        }
        else if (DateTime.TryParse(value, out _))
        {
            return "utcDateTime";
        }
        else if (double.TryParse(value, out _))
        {
            return "decimal";
        }

        return "string";
    }
}