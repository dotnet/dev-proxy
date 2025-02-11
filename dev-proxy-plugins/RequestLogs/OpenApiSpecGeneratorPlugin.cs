// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.EventArguments;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Extensions;
using System.Text.Json;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Writers;
using Microsoft.OpenApi;
using Titanium.Web.Proxy.Http;
using System.Web;
using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions.LanguageModel;
using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class OpenApiSpecGeneratorPluginReportItem
{
    public required string ServerUrl { get; init; }
    public required string FileName { get; init; }
}

public class OpenApiSpecGeneratorPluginReport : List<OpenApiSpecGeneratorPluginReportItem>
{
    public OpenApiSpecGeneratorPluginReport() : base() { }

    public OpenApiSpecGeneratorPluginReport(IEnumerable<OpenApiSpecGeneratorPluginReportItem> collection) : base(collection) { }
}

class GeneratedByOpenApiExtension : IOpenApiExtension
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
internal enum SpecVersion
{
    v2_0,
    v3_0
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SpecFormat
{
    Json,
    Yaml
}

internal class OpenApiSpecGeneratorPluginConfiguration
{
    public bool IncludeOptionsRequests { get; set; } = false;

    public SpecVersion SpecVersion { get; set; } = SpecVersion.v3_0;

    public SpecFormat SpecFormat { get; set; } = SpecFormat.Json;
}

public class OpenApiSpecGeneratorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    // from: https://github.com/jonluca/har-to-openapi/blob/0d44409162c0a127cdaccd60b0a270ecd361b829/src/utils/headers.ts
    private static readonly string[] standardHeaders =
    [
        ":authority",
        ":method",
        ":path",
        ":scheme",
        ":status",
        "a-im",
        "accept",
        "accept-additions",
        "accept-ch",
        "accept-ch-lifetime",
        "accept-charset",
        "accept-datetime",
        "accept-encoding",
        "accept-features",
        "accept-language",
        "accept-patch",
        "accept-post",
        "accept-ranges",
        "access-control-allow-credentials",
        "access-control-allow-headers",
        "access-control-allow-methods",
        "access-control-allow-origin",
        "access-control-expose-headers",
        "access-control-max-age",
        "access-control-request-headers",
        "access-control-request-method",
        "age",
        "allow",
        "alpn",
        "alt-svc",
        "alternate-protocol",
        "alternates",
        "amp-access-control-allow-source-origin",
        "apply-to-redirect-ref",
        "authentication-info",
        "authorization",
        "c-ext",
        "c-man",
        "c-opt",
        "c-pep",
        "c-pep-info",
        "cache-control",
        "ch",
        "connection",
        "content-base",
        "content-disposition",
        "content-dpr",
        "content-encoding",
        "content-id",
        "content-language",
        "content-length",
        "content-location",
        "content-md5",
        "content-range",
        "content-script-type",
        "content-security-policy",
        "content-security-policy-report-only",
        "content-style-type",
        "content-type",
        "content-version",
        "cookie",
        "cookie2",
        "cross-origin-resource-policy",
        "dasl",
        "date",
        "dav",
        "default-style",
        "delta-base",
        "depth",
        "derived-from",
        "destination",
        "differential-id",
        "digest",
        "dnt",
        "dpr",
        "encryption",
        "encryption-key",
        "etag",
        "expect",
        "expect-ct",
        "expires",
        "ext",
        "forwarded",
        "from",
        "front-end-https",
        "getprofile",
        "host",
        "http2-settings",
        "if",
        "if-match",
        "if-modified-since",
        "if-none-match",
        "if-range",
        "if-schedule-tag-match",
        "if-unmodified-since",
        "im",
        "keep-alive",
        "key",
        "label",
        "last-event-id",
        "last-modified",
        "link",
        "link-template",
        "location",
        "lock-token",
        "man",
        "max-forwards",
        "md",
        "meter",
        "mime-version",
        "negotiate",
        "nice",
        "opt",
        "ordering-type",
        "origin",
        "origin-trial",
        "overwrite",
        "p3p",
        "pep",
        "pep-info",
        "pics-label",
        "poe",
        "poe-links",
        "position",
        "pragma",
        "prefer",
        "preference-applied",
        "profileobject",
        "protocol",
        "protocol-info",
        "protocol-query",
        "protocol-request",
        "proxy-authenticate",
        "proxy-authentication-info",
        "proxy-authorization",
        "proxy-connection",
        "proxy-features",
        "proxy-instruction",
        "public",
        "range",
        "redirect-ref",
        "referer",
        "referrer-policy",
        "report-to",
        "retry-after",
        "rw",
        "safe",
        "save-data",
        "schedule-reply",
        "schedule-tag",
        "sec-ch-ua",
        "sec-ch-ua-mobile",
        "sec-ch-ua-platform",
        "sec-fetch-dest",
        "sec-fetch-mode",
        "sec-fetch-site",
        "sec-fetch-user",
        "sec-websocket-accept",
        "sec-websocket-extensions",
        "sec-websocket-key",
        "sec-websocket-protocol",
        "sec-websocket-version",
        "security-scheme",
        "server",
        "server-timing",
        "set-cookie",
        "set-cookie2",
        "setprofile",
        "slug",
        "soapaction",
        "status-uri",
        "strict-transport-security",
        "sunset",
        "surrogate-capability",
        "surrogate-control",
        "tcn",
        "te",
        "timeout",
        "timing-allow-origin",
        "tk",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "upgrade-insecure-requests",
        "uri",
        "user-agent",
        "variant-vary",
        "vary",
        "via",
        "want-digest",
        "warning",
        "www-authenticate",
        "x-att-deviceid",
        "x-csrf-token",
        "x-forwarded-for",
        "x-forwarded-host",
        "x-forwarded-proto",
        "x-frame-options",
        "x-frontend",
        "x-http-method-override",
        "x-powered-by",
        "x-request-id",
        "x-requested-with",
        "x-uidh",
        "x-wap-profile",
        "x-xss-protection"
    ];
    private static readonly string[] authHeaders =
    [
        "access-token",
        "api-key",
        "auth-token",
        "authorization",
        "authorization-token",
        "cookie",
        "key",
        "token",
        "x-access-token",
        "x-access-token",
        "x-api-key",
        "x-auth",
        "x-auth-token",
        "x-csrf-token",
        "secret",
        "x-secret",
        "access-key",
        "api-key",
        "apikey"
    ];

    public override string Name => nameof(OpenApiSpecGeneratorPlugin);
    private readonly OpenApiSpecGeneratorPluginConfiguration _configuration = new();
    public static readonly string GeneratedOpenApiSpecsKey = "GeneratedOpenApiSpecs";

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        Logger.LogInformation("Creating OpenAPI spec from recorded requests...");

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        var openApiDocs = new List<OpenApiDocument>();

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null)
            {
                continue;
            }

            if (!_configuration.IncludeOptionsRequests &&
                string.Equals(request.Context.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping OPTIONS request {url}...", request.Context.Session.HttpClient.Request.RequestUri);
                continue;
            }

            var methodAndUrlString = request.Message.First();
            Logger.LogDebug("Processing request {methodAndUrlString}...", methodAndUrlString);

            try
            {
                var pathItem = GetOpenApiPathItem(request.Context.Session);
                var parametrizedPath = ParametrizePath(pathItem, request.Context.Session.HttpClient.Request.RequestUri);
                var operationInfo = pathItem.Operations.First();
                operationInfo.Value.OperationId = await GetOperationIdAsync(
                    operationInfo.Key.ToString(),
                    request.Context.Session.HttpClient.Request.RequestUri.GetLeftPart(UriPartial.Authority),
                    parametrizedPath
                );
                operationInfo.Value.Description = await GetOperationDescriptionAsync(
                    operationInfo.Key.ToString(),
                    request.Context.Session.HttpClient.Request.RequestUri.GetLeftPart(UriPartial.Authority),
                    parametrizedPath
                );
                AddOrMergePathItem(openApiDocs, pathItem, request.Context.Session.HttpClient.Request.RequestUri, parametrizedPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing request {methodAndUrl}", methodAndUrlString);
            }
        }

        Logger.LogDebug("Serializing OpenAPI docs...");
        var generatedOpenApiSpecs = new Dictionary<string, string>();
        foreach (var openApiDoc in openApiDocs)
        {
            var server = openApiDoc.Servers.First();
            var fileName = GetFileNameFromServerUrl(server.Url, _configuration.SpecFormat);

            var openApiSpecVersion = _configuration.SpecVersion switch
            {
                SpecVersion.v2_0 => OpenApiSpecVersion.OpenApi2_0,
                SpecVersion.v3_0 => OpenApiSpecVersion.OpenApi3_0,
                _ => OpenApiSpecVersion.OpenApi3_0
            };

            var docString = _configuration.SpecFormat switch
            {
                SpecFormat.Json => openApiDoc.SerializeAsJson(openApiSpecVersion),
                SpecFormat.Yaml => openApiDoc.SerializeAsYaml(openApiSpecVersion),
                _ => openApiDoc.SerializeAsJson(openApiSpecVersion)
            };

            Logger.LogDebug("  Writing OpenAPI spec to {fileName}...", fileName);
            File.WriteAllText(fileName, docString);

            generatedOpenApiSpecs.Add(server.Url, fileName);

            Logger.LogInformation("Created OpenAPI spec file {fileName}", fileName);
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

                pathItem.Parameters.Add(new OpenApiParameter
                {
                    Name = parameterName,
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { Type = "string" }
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

    private async Task<string> GetOperationIdAsync(string method, string serverUrl, string parametrizedPath)
    {
        var prompt = $"For the specified request, generate an operation ID, compatible with an OpenAPI spec. Respond with just the ID in plain-text format. For example, for request such as `GET https://api.contoso.com/books/{{books-id}}` you return `getBookById`. For a request like `GET https://api.contoso.com/books/{{books-id}}/authors` you return `getAuthorsForBookById`. Request: {method.ToUpper()} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? id = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            id = await Context.LanguageModelClient.GenerateCompletionAsync(prompt);
        }
        return id?.Response ?? $"{method}{parametrizedPath.Replace('/', '.')}";
    }

    private async Task<string> GetOperationDescriptionAsync(string method, string serverUrl, string parametrizedPath)
    {
        var prompt = $"You're an expert in OpenAPI. You help developers build great OpenAPI specs for use with LLMs. For the specified request, generate a one-sentence description. Respond with just the description. For example, for a request such as `GET https://api.contoso.com/books/{{books-id}}` you return `Get a book by ID`. Request: {method.ToUpper()} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            description = await Context.LanguageModelClient.GenerateCompletionAsync(prompt);
        }
        return description?.Response ?? $"{method} {parametrizedPath}";
    }

    /**
     * Creates an OpenAPI PathItem from an intercepted request and response pair.
     * @param session The intercepted session.
     */
    private OpenApiPathItem GetOpenApiPathItem(SessionEventArgs session)
    {
        var request = session.HttpClient.Request;
        var response = session.HttpClient.Response;

        var resource = GetLastNonTokenSegment(request.RequestUri.Segments);
        var path = new OpenApiPathItem();

        var method = request.Method?.ToUpperInvariant() switch
        {
            "DELETE" => OperationType.Delete,
            "GET" => OperationType.Get,
            "HEAD" => OperationType.Head,
            "OPTIONS" => OperationType.Options,
            "PATCH" => OperationType.Patch,
            "POST" => OperationType.Post,
            "PUT" => OperationType.Put,
            "TRACE" => OperationType.Trace,
            _ => throw new NotSupportedException($"Method {request.Method} is not supported")
        };
        var operation = new OpenApiOperation
        {
            // will be replaced later after the path has been parametrized
            Description = $"{method} {resource}",
            // will be replaced later after the path has been parametrized
            OperationId = $"{method}.{resource}"
        };
        SetParametersFromQueryString(operation, HttpUtility.ParseQueryString(request.RequestUri.Query));
        SetParametersFromRequestHeaders(operation, request.Headers);
        SetRequestBody(operation, request);
        SetResponseFromSession(operation, response);

        path.Operations.Add(method, operation);

        return path;
    }

    private void SetRequestBody(OpenApiOperation operation, Request request)
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
            Content = new Dictionary<string, OpenApiMediaType>
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

    private void SetParametersFromRequestHeaders(OpenApiOperation operation, HeaderCollection headers)
    {
        if (headers is null ||
            !headers.Any())
        {
            Logger.LogDebug("  Request has no headers");
            return;
        }

        Logger.LogDebug("  Processing request headers...");
        foreach (var header in headers)
        {
            var lowerCaseHeaderName = header.Name.ToLowerInvariant();
            if (standardHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping standard header {headerName}", header.Name);
                continue;
            }

            if (authHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping auth header {headerName}", header.Name);
                continue;
            }

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = false,
                Schema = new OpenApiSchema { Type = "string" }
            });
            Logger.LogDebug("    Added header {headerName}", header.Name);
        }
    }

    private void SetParametersFromQueryString(OpenApiOperation operation, NameValueCollection queryParams)
    {
        if (queryParams.AllKeys is null ||
            queryParams.AllKeys.Length == 0)
        {
            Logger.LogDebug("  Request has no query string parameters");
            return;
        }

        Logger.LogDebug("  Processing query string parameters...");
        var dictionary = (queryParams.AllKeys as string[]).ToDictionary(k => k, k => queryParams[k] as object);

        foreach (var parameter in dictionary)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Key,
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = "string" }
            });
            Logger.LogDebug("    Added query string parameter {parameterKey}", parameter.Key);
        }
    }

    private void SetResponseFromSession(OpenApiOperation operation, Response response)
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
        var responseCode = response.StatusCode.ToString();
        if (response.HasBody)
        {
            Logger.LogDebug("    Response has body");

            openApiResponse.Content.Add(GetMediaType(response.ContentType), new OpenApiMediaType
            {
                Schema = GetSchemaFromBody(GetMediaType(response.ContentType), response.BodyString)
            });
        }
        else
        {
            Logger.LogDebug("    Response doesn't have body");
        }

        if (response.Headers is not null && response.Headers.Any())
        {
            Logger.LogDebug("    Response has headers");

            foreach (var header in response.Headers)
            {
                var lowerCaseHeaderName = header.Name.ToLowerInvariant();
                if (standardHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping standard header {headerName}", header.Name);
                    continue;
                }

                if (authHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping auth header {headerName}", header.Name);
                    continue;
                }

                if (openApiResponse.Headers.ContainsKey(header.Name))
                {
                    Logger.LogDebug("    Header {headerName} already exists in response", header.Name);
                    continue;
                }

                openApiResponse.Headers.Add(header.Name, new OpenApiHeader
                {
                    Schema = new OpenApiSchema { Type = "string" }
                });
                Logger.LogDebug("    Added header {headerName}", header.Name);
            }
        }
        else
        {
            Logger.LogDebug("    Response doesn't have headers");
        }

        operation.Responses.Add(responseCode, openApiResponse);
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

    private OpenApiSchema? GetSchemaFromBody(string? contentType, string body)
    {
        if (contentType is null)
        {
            Logger.LogDebug("  No content type to process");
            return null;
        }

        if (contentType.StartsWith("application/json"))
        {
            Logger.LogDebug("    Processing JSON body...");
            return GetSchemaFromJsonString(body);
        }

        return null;
    }

    private void AddOrMergePathItem(IList<OpenApiDocument> openApiDocs, OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        var serverUrl = requestUri.GetLeftPart(UriPartial.Authority);
        var openApiDoc = openApiDocs.FirstOrDefault(d => d.Servers.Any(s => s.Url == serverUrl));

        if (openApiDoc is null)
        {
            Logger.LogDebug("  Creating OpenAPI spec for {serverUrl}...", serverUrl);

            openApiDoc = new OpenApiDocument
            {
                Info = new OpenApiInfo
                {
                    Version = "v1.0",
                    Title = $"{serverUrl} API",
                    Description = $"{serverUrl} API",
                },
                Servers =
                [
                    new OpenApiServer { Url = serverUrl }
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
            Logger.LogDebug("  Found OpenAPI spec for {serverUrl}...", serverUrl);
        }

        if (!openApiDoc.Paths.TryGetValue(parametrizedPath, out OpenApiPathItem? value))
        {
            Logger.LogDebug("  Adding path {parametrizedPath} to OpenAPI spec...", parametrizedPath);
            value = pathItem;
            openApiDoc.Paths.Add(parametrizedPath, value);
            // since we've just added the path, we're done
            return;
        }

        Logger.LogDebug("  Merging path {parametrizedPath} into OpenAPI spec...", parametrizedPath);
        var operation = pathItem.Operations.First();
        AddOrMergeOperation(value, operation.Key, operation.Value);
    }

    private void AddOrMergeOperation(OpenApiPathItem pathItem, OperationType operationType, OpenApiOperation apiOperation)
    {
        if (!pathItem.Operations.TryGetValue(operationType, out OpenApiOperation? value))
        {
            Logger.LogDebug("    Adding operation {operationType} to path...", operationType);

            pathItem.AddOperation(operationType, apiOperation);
            // since we've just added the operation, we're done
            return;
        }

        Logger.LogDebug("    Merging operation {operationType} into path...", operationType);

        var operation = value;

        AddOrMergeParameters(operation, apiOperation.Parameters);
        AddOrMergeRequestBody(operation, apiOperation.RequestBody);
        AddOrMergeResponse(operation, apiOperation.Responses);
    }

    private void AddOrMergeParameters(OpenApiOperation operation, IList<OpenApiParameter> parameters)
    {
        if (parameters is null || !parameters.Any())
        {
            Logger.LogDebug("    No parameters to process");
            return;
        }

        Logger.LogDebug("    Processing parameters for operation...");

        foreach (var parameter in parameters)
        {
            var paramFromOperation = operation.Parameters.FirstOrDefault(p => p.Name == parameter.Name && p.In == parameter.In);
            if (paramFromOperation is null)
            {
                Logger.LogDebug("      Adding parameter {parameterName} to operation...", parameter.Name);
                operation.Parameters.Add(parameter);
                continue;
            }

            Logger.LogDebug("      Merging parameter {parameterName}...", parameter.Name);
            MergeSchema(parameter?.Schema, paramFromOperation?.Schema);
        }
    }

    private void MergeSchema(OpenApiSchema? source, OpenApiSchema? target)
    {
        if (source is null || target is null)
        {
            Logger.LogDebug("        Source or target is null. Skipping...");
            return;
        }

        if (source.Type != "object" || target.Type != "object")
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
                Logger.LogDebug("        Adding property {propertyKey} to schema...", property.Key);
                target.Properties.Add(property);
                continue;
            }

            if (property.Value.Type != "object")
            {
                Logger.LogDebug("        Property already found but is not an object. Skipping...");
                continue;
            }

            Logger.LogDebug("        Merging property {propertyKey}...", property.Key);
            MergeSchema(property.Value, propertyFromTarget.Value);
        }
    }

    private void AddOrMergeRequestBody(OpenApiOperation operation, OpenApiRequestBody requestBody)
    {
        if (requestBody is null || !requestBody.Content.Any())
        {
            Logger.LogDebug("    No request body to process");
            return;
        }

        var requestBodyType = requestBody.Content.FirstOrDefault().Key;
        operation.RequestBody.Content.TryGetValue(requestBodyType, out OpenApiMediaType? bodyFromOperation);

        if (bodyFromOperation is null)
        {
            Logger.LogDebug("    Adding request body to operation...");

            operation.RequestBody.Content.Add(requestBody.Content.FirstOrDefault());
            // since we've just added the request body, we're done
            return;
        }

        Logger.LogDebug("    Merging request body into operation...");
        MergeSchema(bodyFromOperation.Schema, requestBody.Content.FirstOrDefault().Value.Schema);
    }

    private void AddOrMergeResponse(OpenApiOperation operation, OpenApiResponses apiResponses)
    {
        if (apiResponses is null)
        {
            Logger.LogDebug("    No response to process");
            return;
        }

        var apiResponseInfo = apiResponses.FirstOrDefault();
        var apiResponseStatusCode = apiResponseInfo.Key;
        var apiResponse = apiResponseInfo.Value;
        operation.Responses.TryGetValue(apiResponseStatusCode, out OpenApiResponse? responseFromOperation);

        if (responseFromOperation is null)
        {
            Logger.LogDebug("    Adding response {apiResponseStatusCode} to operation...", apiResponseStatusCode);

            operation.Responses.Add(apiResponseStatusCode, apiResponse);
            // since we've just added the response, we're done
            return;
        }

        if (!apiResponse.Content.Any())
        {
            Logger.LogDebug("    No response content to process");
            return;
        }

        var apiResponseContentType = apiResponse.Content.First().Key;
        responseFromOperation.Content.TryGetValue(apiResponseContentType, out OpenApiMediaType? contentFromOperation);

        if (contentFromOperation is null)
        {
            Logger.LogDebug("    Adding response {apiResponseContentType} to {apiResponseStatusCode} to response...", apiResponseContentType, apiResponseStatusCode);

            responseFromOperation.Content.Add(apiResponse.Content.First());
            // since we've just added the content, we're done
            return;
        }

        Logger.LogDebug("    Merging response {apiResponseStatusCode}/{apiResponseContentType} into operation...", apiResponseStatusCode, apiResponseContentType);
        MergeSchema(contentFromOperation.Schema, apiResponse.Content.First().Value.Schema);
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
        var fileName = $"{uri.Host}-{DateTime.Now:yyyyMMddHHmmss}.{ext}";
        return fileName;
    }

    private static OpenApiSchema GetSchemaFromJsonString(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            JsonElement root = doc.RootElement;
            var schema = GetSchemaFromJsonElement(root);
            return schema;
        }
        catch
        {
            return new OpenApiSchema
            {
                Type = "object"
            };
        }
    }

    private static OpenApiSchema GetSchemaFromJsonElement(JsonElement jsonElement)
    {
        var schema = new OpenApiSchema();

        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.String:
                schema.Type = "string";
                break;
            case JsonValueKind.Number:
                schema.Type = "number";
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                schema.Type = "boolean";
                break;
            case JsonValueKind.Object:
                schema.Type = "object";
                schema.Properties = jsonElement.EnumerateObject()
                  .ToDictionary(p => p.Name, p => GetSchemaFromJsonElement(p.Value));
                break;
            case JsonValueKind.Array:
                schema.Type = "array";
                schema.Items = GetSchemaFromJsonElement(jsonElement.EnumerateArray().FirstOrDefault());
                break;
            default:
                schema.Type = "object";
                break;
        }

        return schema;
    }
}
