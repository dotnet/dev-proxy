// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace DevProxy.Plugins.Generation;

sealed class GeneratedByOpenApiExtension : IOpenApiExtension
{
    public void Write(IOpenApiWriter writer, OpenApiSpecVersion specVersion)
    {
        writer.WriteStartObject();
        writer.WriteProperty("toolName", "Dev Proxy");
        writer.WriteProperty("toolVersion", ProxyUtils.ProductVersion);
        writer.WriteEndObject();
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecVersion
{
#pragma warning disable CA1707
    v2_0,
    v3_0,
    v3_1,
    v3_2
#pragma warning restore CA1707
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecFormat
{
    Json,
    Yaml
}

public sealed class OpenApiSpecGeneratorPluginConfiguration
{
    public bool IncludeOptionsRequests { get; set; }
    public SpecFormat SpecFormat { get; set; } = SpecFormat.Json;
    public SpecVersion SpecVersion { get; set; } = SpecVersion.v3_0;
    public bool IgnoreResponseTypes { get; set; }
    public IEnumerable<string> IncludeParameters { get; set; } = [];
}

public sealed class OpenApiSpecGeneratorPlugin(
    HttpClient httpClient,
    ILogger<OpenApiSpecGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<OpenApiSpecGeneratorPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public static readonly string GeneratedOpenApiSpecsKey = "GeneratedOpenApiSpecs";

    public override string Name => nameof(OpenApiSpecGeneratorPlugin);

    private const string BearerSchemeName = "BearerAuth";

    private sealed class SecurityState
    {
        public HashSet<OpenApiOperation> AnonymousOperations { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<OpenApiSecurityRequirement, string> RequirementKeys { get; } = new(ReferenceEqualityComparer.Instance);
    }

    public override async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        Logger.LogInformation("Creating OpenAPI spec from recorded requests...");

        var openApiDocs = new List<OpenApiDocument>();
        var securityState = new SecurityState();

        foreach (var request in e.RequestLogs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null ||
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.Request.RequestUri.AbsoluteUri))
            {
                continue;
            }

            if (!Configuration.IncludeOptionsRequests &&
                string.Equals(request.Context.Session.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping OPTIONS request {Url}...", request.Context.Session.Request.RequestUri);
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {MethodAndUrlString}...", methodAndUrlString);

            try
            {
                var detectedAuth = AuthDetector.Detect(request.Context.Session.Request, Logger);
                var pathItem = GetOpenApiPathItem(request.Context.Session, detectedAuth);
                var parametrizedPath = ParametrizePath(pathItem, request.Context.Session.Request.RequestUri);
                if (pathItem.Operations is null)
                {
                    Logger.LogDebug("No operations found for request {MethodAndUrlString}. Skipping...", methodAndUrlString);
                    continue;
                }
                var operationInfo = pathItem.Operations.First();
                operationInfo.Value.OperationId = await GetOperationIdAsync(
                    operationInfo.Key.ToString(),
                    request.Context.Session.Request.RequestUri.GetLeftPart(UriPartial.Authority),
                    parametrizedPath,
                    cancellationToken
                );
                operationInfo.Value.Description = await GetOperationDescriptionAsync(
                    operationInfo.Key.ToString(),
                    request.Context.Session.Request.RequestUri.GetLeftPart(UriPartial.Authority),
                    parametrizedPath,
                    cancellationToken
                );
                AddOrMergePathItem(openApiDocs, pathItem, request.Context.Session.Request.RequestUri, parametrizedPath);
                AddOrMergeSecurity(openApiDocs, pathItem, request.Context.Session, parametrizedPath, detectedAuth, securityState);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing request {MethodAndUrl}", methodAndUrlString);
            }
        }

        Logger.LogDebug("Serializing OpenAPI docs...");
        var generatedOpenApiSpecs = new Dictionary<string, string>();
        foreach (var openApiDoc in openApiDocs)
        {
            if (openApiDoc.Servers is null || !openApiDoc.Servers.Any())
            {
                Logger.LogDebug("OpenAPI doc has no servers. Skipping...");
                continue;
            }
            var server = openApiDoc.Servers.First();
            if (string.IsNullOrEmpty(server.Url))
            {
                Logger.LogDebug("Server URL is null or empty. Skipping...");
                continue;
            }
            var fileName = GetFileNameFromServerUrl(server.Url, Configuration.SpecFormat);

            var openApiSpecVersion = Configuration.SpecVersion switch
            {
                SpecVersion.v2_0 => OpenApiSpecVersion.OpenApi2_0,
                SpecVersion.v3_0 => OpenApiSpecVersion.OpenApi3_0,
                SpecVersion.v3_1 => OpenApiSpecVersion.OpenApi3_1,
                SpecVersion.v3_2 => OpenApiSpecVersion.OpenApi3_2,
                _ => OpenApiSpecVersion.OpenApi3_0
            };

            var docString = Configuration.SpecFormat switch
            {
                SpecFormat.Json => await openApiDoc.SerializeAsJsonAsync(openApiSpecVersion, cancellationToken),
                SpecFormat.Yaml => await openApiDoc.SerializeAsYamlAsync(openApiSpecVersion, cancellationToken),
                _ => await openApiDoc.SerializeAsJsonAsync(openApiSpecVersion, cancellationToken)
            };

            Logger.LogDebug("  Writing OpenAPI spec to {FileName}...", fileName);
            await File.WriteAllTextAsync(fileName, docString, cancellationToken);

            generatedOpenApiSpecs.Add(server.Url, fileName);

            Logger.LogInformation("Created OpenAPI spec file {FileName}", fileName);
        }

        StoreReport(new OpenApiSpecGeneratorPluginReport(
            generatedOpenApiSpecs
            .Select(kvp => new OpenApiSpecGeneratorPluginReportItem
            {
                ServerUrl = kvp.Key,
                FileName = kvp.Value
            })), e);

        // store the generated OpenAPI specs in the global data
        // for use by other plugins
        e.GlobalData[GeneratedOpenApiSpecsKey] = generatedOpenApiSpecs;

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    private async Task<string> GetOperationIdAsync(string method, string serverUrl, string parametrizedPath, CancellationToken cancellationToken)
    {
        var id = await languageModelClient.GenerateChatCompletionAsync("api_operation_id", new()
        {
            { "request", $"{method.ToUpperInvariant()} {serverUrl}{parametrizedPath}" }
        }, cancellationToken);
        return id?.Response ?? $"{method}{parametrizedPath.Replace('/', '.')}";
    }

    private async Task<string> GetOperationDescriptionAsync(string method, string serverUrl, string parametrizedPath, CancellationToken cancellationToken)
    {
        var description = await languageModelClient.GenerateChatCompletionAsync("api_operation_description", new()
        {
            { "request", $"{method.ToUpperInvariant()} {serverUrl}{parametrizedPath}" }
        },
        cancellationToken);
        return description?.Response ?? $"{method} {parametrizedPath}";
    }

    /**
     * Creates an OpenAPI PathItem from an intercepted request and response pair.
     * @param session The intercepted session.
     */
    private OpenApiPathItem GetOpenApiPathItem(IProxySession session, DetectedAuth? detectedAuth)
    {
        var request = session.Request;
        var response = session.Response!;

        var resource = GetLastNonTokenSegment(request.RequestUri.Segments);
        var path = new OpenApiPathItem
        {
            Operations = []
        };

        var method = request.Method?.ToUpperInvariant() switch
        {
            "DELETE" => HttpMethod.Delete,
            "GET" => HttpMethod.Get,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            "PATCH" => HttpMethod.Patch,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "QUERY" => HttpMethod.Query,
            "TRACE" => HttpMethod.Trace,
            _ => throw new NotSupportedException($"Method {request.Method} is not supported")
        };
        var operation = new OpenApiOperation
        {
            // will be replaced later after the path has been parametrized
            Description = $"{method} {resource}",
            // will be replaced later after the path has been parametrized
            OperationId = $"{method}.{resource}"
        };
        var authQueryParameter = detectedAuth is DetectedApiKeyAuth { In: DetectedApiKeyLocation.Query } apiKeyAuth ?
            apiKeyAuth.Name : null;
        SetParametersFromQueryString(operation, HttpUtility.ParseQueryString(request.RequestUri.Query), authQueryParameter);
        SetParametersFromRequestHeaders(operation, request.Headers);
        SetRequestBody(operation, request);
        SetResponseFromSession(operation, response);

        path.Operations?.Add(method, operation);

        return path;
    }

    private void SetRequestBody(OpenApiOperation operation, IHttpRequest request)
    {
        if (!request.HasBody)
        {
            Logger.LogDebug("  Request has no body");
            return;
        }

        if (request.ContentType is null)
        {
            Logger.LogDebug("  Request has no content type");
            return;
        }

        Logger.LogDebug("  Processing request body...");
        operation.RequestBody = new OpenApiRequestBody
        {
            Content = new Dictionary<string, IOpenApiMediaType>
            {
                {
                    GetMediaType(request.ContentType),
                    new OpenApiMediaType
                    {
                        Schema = GetSchemaFromBody(GetMediaType(request.ContentType), request.BodyString)
                    }
                }
            }
        };
    }

    private void SetParametersFromRequestHeaders(OpenApiOperation operation, IHeaderCollection headers)
    {
        if (headers is null ||
            headers.Count == 0)
        {
            Logger.LogDebug("  Request has no headers");
            return;
        }

        Logger.LogDebug("  Processing request headers...");
        foreach (var header in headers)
        {
            var lowerCaseHeaderName = header.Name.ToLowerInvariant();
            if (Models.Http.StandardHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping standard header {HeaderName}", header.Name);
                continue;
            }

            if (Models.Http.AuthHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping auth header {HeaderName}", header.Name);
                continue;
            }

            operation.Parameters?.Add(new OpenApiParameter()
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = false,
                Schema = new OpenApiSchema() { Type = JsonSchemaType.String }
            });
            Logger.LogDebug("    Added header {HeaderName}", header.Name);
        }
    }

    private void SetParametersFromQueryString(OpenApiOperation operation, NameValueCollection queryParams, string? authQueryParameter)
    {
        if (queryParams.AllKeys is null ||
            queryParams.AllKeys.Length == 0)
        {
            Logger.LogDebug("  Request has no query string parameters");
            return;
        }

        Logger.LogDebug("  Processing query string parameters...");
        var dictionary = queryParams.AllKeys
            .Where(k => k is not null).Cast<string>()
            .ToDictionary(k => k, k => queryParams[k] as object);

        foreach (var (key, value) in dictionary)
        {
            if (string.Equals(key, authQueryParameter, StringComparison.Ordinal))
            {
                Logger.LogDebug("    Skipping auth query string parameter {ParameterKey}", key);
                continue;
            }

            var isRequired = Configuration.IncludeParameters.Any(p => string.Equals(p, key, StringComparison.Ordinal));

            OpenApiParameter parameter = new()
            {
                Name = key,
                In = ParameterLocation.Query,
                Required = isRequired,
                Schema = new OpenApiSchema() { Type = JsonSchemaType.String }
            };
            SetParameterDefault(parameter, value);

            operation.Parameters?.Add(parameter);
            Logger.LogDebug("    Added query string parameter {ParameterKey}", key);
        }
    }

    private static void SetParameterDefault(OpenApiParameter parameter, object? value)
    {
        if (!parameter.Required || value is null)
        {
            return;
        }
        if (parameter.Schema is OpenApiSchema schema)
        {
            schema.Default = JsonValue.Create(value.ToString());
        }
    }

    private void SetResponseFromSession(OpenApiOperation operation, IHttpResponse response)
    {
        if (response is null)
        {
            Logger.LogDebug("  No response to process");
            return;
        }

        Logger.LogDebug("  Processing response...");

        var openApiResponse = new OpenApiResponse
        {
            Description = response.StatusDescription
        };
        var responseCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        if (response.HasBody)
        {
            Logger.LogDebug("    Response has body");

            openApiResponse.Content?.Add(GetMediaType(response.ContentType), new OpenApiMediaType()
            {
                Schema = GetSchemaFromBody(GetMediaType(response.ContentType), response.BodyString)
            });
        }
        else
        {
            Logger.LogDebug("    Response doesn't have body");
        }

        if (response.Headers is not null && response.Headers.Count > 0)
        {
            Logger.LogDebug("    Response has headers");

            foreach (var header in response.Headers)
            {
                var lowerCaseHeaderName = header.Name.ToLowerInvariant();
                if (Models.Http.StandardHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping standard header {HeaderName}", header.Name);
                    continue;
                }

                if (Models.Http.AuthHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping auth header {HeaderName}", header.Name);
                    continue;
                }

                if (openApiResponse.Headers?.ContainsKey(header.Name) == true)
                {
                    Logger.LogDebug("    Header {HeaderName} already exists in response", header.Name);
                    continue;
                }

                openApiResponse.Headers?.Add(header.Name, new OpenApiHeader()
                {
                    Schema = new OpenApiSchema() { Type = JsonSchemaType.String }
                });
                Logger.LogDebug("    Added header {HeaderName}", header.Name);
            }
        }
        else
        {
            Logger.LogDebug("    Response doesn't have headers");
        }

        operation.Responses?.Add(responseCode, openApiResponse);
    }

    private OpenApiSchema? GetSchemaFromBody(string? contentType, string body)
    {
        if (contentType is null)
        {
            Logger.LogDebug("  No content type to process");
            return null;
        }

        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("    Processing JSON body...");
            if (Configuration.IgnoreResponseTypes)
            {
                Logger.LogDebug("      Ignoring response types");
                return new()
                {
                    Type = JsonSchemaType.String
                };
            }

            return GetSchemaFromJsonString(body);
        }

        return null;
    }

    private void AddOrMergePathItem(List<OpenApiDocument> openApiDocs, OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        var serverUrl = requestUri.GetLeftPart(UriPartial.Authority);
        var openApiDoc = openApiDocs.FirstOrDefault(d => d.Servers?.Any(s => s.Url == serverUrl) == true);

        if (openApiDoc is null)
        {
            Logger.LogDebug("  Creating OpenAPI spec for {ServerUrl}...", serverUrl);

            openApiDoc = new OpenApiDocument
            {
                Info = new()
                {
                    Version = "v1.0",
                    Title = $"{serverUrl} API",
                    Description = $"{serverUrl} API",
                },
                Servers =
                [
                    new() { Url = serverUrl }
                ],
                Paths = [],
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                    { "x-ms-generated-by", new GeneratedByOpenApiExtension() }
                }
            };
            openApiDocs.Add(openApiDoc);
        }
        else
        {
            Logger.LogDebug("  Found OpenAPI spec for {ServerUrl}...", serverUrl);
        }

        if (!openApiDoc.Paths.TryGetValue(parametrizedPath, out var value))
        {
            Logger.LogDebug("  Adding path {ParametrizedPath} to OpenAPI spec...", parametrizedPath);
            value = pathItem;
            openApiDoc.Paths.Add(parametrizedPath, value);
        }
        else
        {
            Logger.LogDebug("  Merging path {ParametrizedPath} into OpenAPI spec...", parametrizedPath);
            var newOperation = pathItem.Operations?.First();
            if (newOperation is not null)
            {
                AddOrMergeOperation((OpenApiPathItem)value, newOperation.Value.Key, newOperation.Value.Value);
            }
        }
    }

    private void AddOrMergeSecurity(
        List<OpenApiDocument> openApiDocs,
        OpenApiPathItem pathItem,
        IProxySession session,
        string parametrizedPath,
        DetectedAuth? detectedAuth,
        SecurityState securityState)
    {
        var serverUrl = session.Request.RequestUri.GetLeftPart(UriPartial.Authority);
        var openApiDoc = openApiDocs.FirstOrDefault(d => d.Servers?.Any(s => s.Url == serverUrl) == true);
        var method = pathItem.Operations?.FirstOrDefault().Key;
        if (openApiDoc is null ||
            method is null ||
            !openApiDoc.Paths.TryGetValue(parametrizedPath, out var docPathItem) ||
            docPathItem.Operations?.TryGetValue(method, out var operation) != true ||
            operation is null)
        {
            return;
        }

        if (detectedAuth is null)
        {
            var statusCode = (int)(session.Response?.StatusCode ?? 0);
            // cookies aren't detected as auth, but might authenticate the request
            var hasCookie = session.Request.Headers.Any(h => h.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase));
            if (statusCode is >= 200 and < 300 && !hasCookie)
            {
                _ = securityState.AnonymousOperations.Add(operation);
                AddAnonymousSecurityRequirement(operation);
            }
            return;
        }

        var (schemeName, scopes) = AddOrMergeSecurityScheme(openApiDoc, detectedAuth);
        // requirements for different OAuth2 flows are alternatives, so we keep
        // them separate and only merge scopes within the same flow
        var requirementKey = detectedAuth is DetectedOAuth2Auth oauth2 ?
            $"{schemeName}:{oauth2.FlowType}" : schemeName;

        operation.Security ??= [];
        var existingRequirement = operation.Security
            .FirstOrDefault(r => securityState.RequirementKeys.TryGetValue(r, out var key) && key == requirementKey);
        if (existingRequirement is null)
        {
            Logger.LogDebug("    Adding security requirement {RequirementKey} to operation...", requirementKey);
            var requirement = new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(schemeName, openApiDoc)] = [.. scopes]
            };
            securityState.RequirementKeys[requirement] = requirementKey;
            operation.Security.Add(requirement);
        }
        else
        {
            Logger.LogDebug("    Merging security requirement {RequirementKey} into operation...", requirementKey);
            var requirementScopes = existingRequirement.First().Value;
            foreach (var scope in scopes.Where(s => !requirementScopes.Contains(s)))
            {
                requirementScopes.Add(scope);
            }
        }

        if (securityState.AnonymousOperations.Contains(operation))
        {
            AddAnonymousSecurityRequirement(operation);
        }
    }

    // an empty requirement marks authentication as optional for operations
    // that were also called successfully without authentication
    private void AddAnonymousSecurityRequirement(OpenApiOperation operation)
    {
        if (operation.Security is null ||
            operation.Security.Count == 0 ||
            operation.Security.Any(r => r.Count == 0))
        {
            return;
        }

        Logger.LogDebug("    Marking authentication as optional for operation...");
        operation.Security.Add(new OpenApiSecurityRequirement());
    }

    private (string SchemeName, IReadOnlyList<string> Scopes) AddOrMergeSecurityScheme(OpenApiDocument openApiDoc, DetectedAuth detectedAuth)
    {
        var (schemeName, scheme, scopes) = detectedAuth switch
        {
            DetectedApiKeyAuth apiKey => (
                GetApiKeySchemeName(apiKey),
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    Name = apiKey.Name,
                    In = apiKey.In == DetectedApiKeyLocation.Query ? ParameterLocation.Query : ParameterLocation.Header
                },
                []),
            DetectedBearerAuth => (
                BearerSchemeName,
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer"
                },
                []),
            DetectedOAuth2Auth oauth2 => (
                GetOAuth2SchemeName(oauth2),
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,
                    Flows = new OpenApiOAuthFlows()
                },
                oauth2.Scopes),
            _ => throw new NotSupportedException($"Auth type {detectedAuth.GetType().Name} is not supported")
        };

        openApiDoc.Components ??= new OpenApiComponents();
        openApiDoc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        if (openApiDoc.Components.SecuritySchemes.TryGetValue(schemeName, out var existingScheme))
        {
            Logger.LogDebug("    Found security scheme {SchemeName}", schemeName);
            scheme = existingScheme as OpenApiSecurityScheme ?? scheme;
        }
        else
        {
            Logger.LogDebug("    Adding security scheme {SchemeName}...", schemeName);
            _ = openApiDoc.AddComponent(schemeName, scheme);
        }

        if (detectedAuth is DetectedOAuth2Auth oauth2Auth)
        {
            MergeOAuth2Flow(scheme, oauth2Auth);
        }
        else if (detectedAuth is DetectedBearerAuth { IsJwt: true })
        {
            scheme.BearerFormat = "JWT";
        }

        return (schemeName, scopes);
    }

    // Swagger 2.0 allows only one flow per security scheme
    private string GetOAuth2SchemeName(DetectedOAuth2Auth oauth2) =>
        Configuration.SpecVersion == SpecVersion.v2_0 && oauth2.FlowType == DetectedOAuth2FlowType.ClientCredentials ?
            $"{oauth2.Name}_clientCredentials" : oauth2.Name;

    private static void MergeOAuth2Flow(OpenApiSecurityScheme scheme, DetectedOAuth2Auth oauth2)
    {
        scheme.Flows ??= new OpenApiOAuthFlows();
        var flow = oauth2.FlowType == DetectedOAuth2FlowType.ClientCredentials ?
            scheme.Flows.ClientCredentials ??= new OpenApiOAuthFlow() :
            scheme.Flows.AuthorizationCode ??= new OpenApiOAuthFlow();

        if (oauth2.FlowType == DetectedOAuth2FlowType.AuthorizationCode)
        {
            flow.AuthorizationUrl ??= ToAbsoluteUri(oauth2.AuthorizationUrl);
        }
        flow.TokenUrl ??= ToAbsoluteUri(oauth2.TokenUrl);
        flow.RefreshUrl ??= ToAbsoluteUri(oauth2.RefreshUrl);
        flow.Scopes ??= new Dictionary<string, string>();
        foreach (var scope in oauth2.Scopes)
        {
            _ = flow.Scopes.TryAdd(scope, scope);
        }
    }

    private static Uri? ToAbsoluteUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;

    private static string GetApiKeySchemeName(DetectedApiKeyAuth apiKey)
    {
        var location = apiKey.In == DetectedApiKeyLocation.Query ? "query" : "header";
        var name = new string([.. apiKey.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return $"ApiKeyAuth_{location}_{name}";
    }

    private void AddOrMergeOperation(OpenApiPathItem pathItem, HttpMethod httpMethod, OpenApiOperation apiOperation)
    {
        if (pathItem.Operations?.TryGetValue(httpMethod, out var value) != true)
        {
            Logger.LogDebug("    Adding operation {OperationType} to path...", httpMethod);

            pathItem.AddOperation(httpMethod, apiOperation);
            // since we've just added the operation, we're done
            return;
        }

        Logger.LogDebug("    Merging operation {OperationType} into path...", httpMethod);

        var operation = value!;

        AddOrMergeParameters(operation, apiOperation.Parameters);
        AddOrMergeRequestBody(operation, (OpenApiRequestBody?)apiOperation.RequestBody);
        AddOrMergeResponse(operation, apiOperation.Responses);
    }

    private void AddOrMergeParameters(OpenApiOperation operation, IList<IOpenApiParameter>? parameters)
    {
        if (parameters is null || !parameters.Any())
        {
            Logger.LogDebug("    No parameters to process");
            return;
        }

        Logger.LogDebug("    Processing parameters for operation...");

        foreach (var parameter in parameters)
        {
            var paramFromOperation = operation.Parameters?.FirstOrDefault(p => p.Name == parameter.Name && p.In == parameter.In);
            if (paramFromOperation is null)
            {
                Logger.LogDebug("      Adding parameter {ParameterName} to operation...", parameter.Name);
                operation.Parameters?.Add(parameter);
                continue;
            }

            Logger.LogDebug("      Merging parameter {ParameterName}...", parameter.Name);
            MergeSchema(parameter?.Schema, paramFromOperation.Schema);
        }
    }

    private void MergeSchema(IOpenApiSchema? source, IOpenApiSchema? target)
    {
        if (source is null || target is null)
        {
            Logger.LogDebug("        Source or target is null. Skipping...");
            return;
        }

        if (source.Type != JsonSchemaType.Object || target.Type != JsonSchemaType.Object)
        {
            Logger.LogDebug("        Source or target schema is not an object. Skipping...");
            return;
        }

        if (source.Properties is null || !source.Properties.Any())
        {
            Logger.LogDebug("        Source has no properties. Skipping...");
            return;
        }

        if (target.Properties is null || !target.Properties.Any())
        {
            Logger.LogDebug("        Target has no properties. Skipping...");
            return;
        }

        foreach (var property in source.Properties)
        {
            var propertyFromTarget = target.Properties.FirstOrDefault(p => p.Key == property.Key);
            if (propertyFromTarget.Value is null)
            {
                Logger.LogDebug("        Adding property {PropertyKey} to schema...", property.Key);
                target.Properties.Add(property);
                continue;
            }

            if (property.Value.Type != JsonSchemaType.Object)
            {
                Logger.LogDebug("        Property already found but is not an object. Skipping...");
                continue;
            }

            Logger.LogDebug("        Merging property {PropertyKey}...", property.Key);
            MergeSchema(property.Value, propertyFromTarget.Value);
        }
    }

    private void AddOrMergeRequestBody(OpenApiOperation operation, OpenApiRequestBody? requestBody)
    {
        if (requestBody is null || requestBody.Content is null || !requestBody.Content.Any())
        {
            Logger.LogDebug("    No request body to process");
            return;
        }

        var requestBodyType = requestBody.Content.FirstOrDefault().Key;
        IOpenApiMediaType? bodyFromOperation = null;
        if (operation.RequestBody?.Content is not null)
        {
            _ = operation.RequestBody.Content.TryGetValue(requestBodyType, out bodyFromOperation);
        }

        if (bodyFromOperation is null)
        {
            Logger.LogDebug("    Adding request body to operation...");

            operation.RequestBody ??= new OpenApiRequestBody
            {
                Content = new Dictionary<string, IOpenApiMediaType>()
            };
            operation.RequestBody.Content!.Add(requestBody.Content.FirstOrDefault());
            // since we've just added the request body, we're done
            return;
        }

        Logger.LogDebug("    Merging request body into operation...");
        MergeSchema(bodyFromOperation.Schema, requestBody.Content.FirstOrDefault().Value.Schema);
    }

    private void AddOrMergeResponse(OpenApiOperation operation, OpenApiResponses? apiResponses)
    {
        if (apiResponses is null)
        {
            Logger.LogDebug("    No response to process");
            return;
        }

        var apiResponseInfo = apiResponses.FirstOrDefault();
        var apiResponseStatusCode = apiResponseInfo.Key;
        var apiResponse = apiResponseInfo.Value;
        IOpenApiResponse? responseFromOperation = null;
        if (operation.Responses is not null)
        {
            _ = operation.Responses.TryGetValue(apiResponseStatusCode, out responseFromOperation);
        }

        if (responseFromOperation is null)
        {
            Logger.LogDebug("    Adding response {ApiResponseStatusCode} to operation...", apiResponseStatusCode);

            operation.Responses?.Add(apiResponseStatusCode, apiResponse);
            // since we've just added the response, we're done
            return;
        }

        if (apiResponse.Content is null || !apiResponse.Content.Any())
        {
            Logger.LogDebug("    No response content to process");
            return;
        }

        var apiResponseContentType = apiResponse.Content.First().Key;
        IOpenApiMediaType? contentFromOperation = null;
        if (responseFromOperation.Content is not null)
        {
            _ = responseFromOperation.Content.TryGetValue(apiResponseContentType, out contentFromOperation);
        }

        if (contentFromOperation is null)
        {
            Logger.LogDebug("    Adding response {ApiResponseContentType} to {ApiResponseStatusCode} to response...", apiResponseContentType, apiResponseStatusCode);

            responseFromOperation.Content?.Add(apiResponse.Content.First());
            // since we've just added the content, we're done
            return;
        }

        Logger.LogDebug("    Merging response {ApiResponseStatusCode}/{ApiResponseContentType} into operation...", apiResponseStatusCode, apiResponseContentType);
        MergeSchema(contentFromOperation.Schema, apiResponse.Content.First().Value.Schema);
    }

    /**
     * Replaces segments in the request URI, that match predefined patters,
     * with parameters and adds them to the OpenAPI PathItem.
     * @param pathItem The OpenAPI PathItem to parametrize.
     * @param requestUri The request URI.
     * @returns The parametrized server-relative URL
     */
    private static string ParametrizePath(OpenApiPathItem pathItem, Uri requestUri)
    {
        var segments = requestUri.Segments;
        var previousSegment = "item";

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = requestUri.Segments[i].Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            if (IsParametrizable(segment))
            {
                var parameterName = $"{previousSegment}-id";
                segments[i] = $"{{{parameterName}}}{(requestUri.Segments[i].EndsWith('/') ? "/" : "")}";

                pathItem.Parameters?.Add(new OpenApiParameter
                {
                    Name = parameterName,
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            }
            else
            {
                previousSegment = segment;
            }
        }

        return string.Join(string.Empty, segments);
    }

    private static bool IsParametrizable(string segment)
    {
        return Guid.TryParse(segment.Trim('/'), out _) ||
          int.TryParse(segment.Trim('/'), out _);
    }

    private static string GetLastNonTokenSegment(string[] segments)
    {
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            if (!IsParametrizable(segment))
            {
                return segment;
            }
        }

        return "item";
    }

    private static string GetMediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return contentType ?? "";
        }

        var mediaType = contentType.Split(';').First().Trim();
        return mediaType;
    }

    private static string GetFileNameFromServerUrl(string serverUrl, SpecFormat format)
    {
        var uri = new Uri(serverUrl);
        var ext = format switch
        {
            SpecFormat.Json => "json",
            SpecFormat.Yaml => "yaml",
            _ => "json"
        };
        var fileName = $"{uri.Host}-{uri.Port}-{DateTime.Now:yyyyMMddHHmmss}.{ext}";
        return fileName;
    }

    private static OpenApiSchema GetSchemaFromJsonString(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString, ProxyUtils.JsonDocumentOptions);
            var root = doc.RootElement;
            var schema = GetSchemaFromJsonElement(root);
            return schema;
        }
        catch
        {
            return new()
            {
                Type = JsonSchemaType.Object
            };
        }
    }

    private static OpenApiSchema GetSchemaFromJsonElement(JsonElement jsonElement)
    {
        var schema = new OpenApiSchema();

#pragma warning disable IDE0010
        switch (jsonElement.ValueKind)
#pragma warning restore IDE0010
        {
            case JsonValueKind.String:
                schema.Type = JsonSchemaType.String;
                break;
            case JsonValueKind.Number:
                schema.Type = JsonSchemaType.Number;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                schema.Type = JsonSchemaType.Boolean;
                break;
            case JsonValueKind.Object:
                schema.Type = JsonSchemaType.Object;
                schema.Properties = jsonElement.EnumerateObject()
                  .ToDictionary(p => p.Name, p => (IOpenApiSchema)GetSchemaFromJsonElement(p.Value));
                break;
            case JsonValueKind.Array:
                schema.Type = JsonSchemaType.Array;
                schema.Items = GetSchemaFromJsonElement(jsonElement.EnumerateArray().FirstOrDefault());
                break;
            default:
                schema.Type = JsonSchemaType.Object;
                break;
        }

        return schema;
    }
}
