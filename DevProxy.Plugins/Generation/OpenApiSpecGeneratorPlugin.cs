// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

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
    v3_0
#pragma warning restore CA1707
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecFormat
{
    Json,
    Yaml
}

public class OpenApiSpecGeneratorPluginConfiguration
{
    public bool IncludeOptionsRequests { get; set; }
    public SpecFormat SpecFormat { get; set; } = SpecFormat.Json;
    public SpecVersion SpecVersion { get; set; } = SpecVersion.v3_0;
}

public class OpenApiSpecGeneratorPlugin(
    ILogger<OpenApiSpecGeneratorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<OpenApiSpecGeneratorPluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    public static readonly string GeneratedOpenApiSpecsKey = "GeneratedOpenApiSpecs";

    public override string Name => nameof(OpenApiSpecGeneratorPlugin);

    public override async Task AfterRecordingStopAsync(RecordingArgs e)
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


        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
                request.Context is null ||
                request.Context.Session is null ||
                !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                continue;
            }

            if (!Configuration.IncludeOptionsRequests &&
                string.Equals(request.Context.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping OPTIONS request {Url}...", request.Context.Session.HttpClient.Request.RequestUri);
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {MethodAndUrlString}...", methodAndUrlString);

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
                var processedPathItem = ProcessPathItem(pathItem, request.Context.Session.HttpClient.Request.RequestUri, parametrizedPath);
                AddOrMergePathItem(openApiDocs, processedPathItem, request.Context.Session.HttpClient.Request.RequestUri, parametrizedPath);
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
            // Allow derived plugins to post-process the OpenApiDocument (above the path level)
            ProcessOpenApiDocument(openApiDoc);

            var server = openApiDoc.Servers.First();
            var fileName = GetFileNameFromServerUrl(server.Url, Configuration.SpecFormat);

            var openApiSpecVersion = Configuration.SpecVersion switch
            {
                SpecVersion.v2_0 => OpenApiSpecVersion.OpenApi2_0,
                SpecVersion.v3_0 => OpenApiSpecVersion.OpenApi3_0,
                _ => OpenApiSpecVersion.OpenApi3_0
            };

            var docString = Configuration.SpecFormat switch
            {
                SpecFormat.Json => openApiDoc.SerializeAsJson(openApiSpecVersion),
                SpecFormat.Yaml => openApiDoc.SerializeAsYaml(openApiSpecVersion),
                _ => openApiDoc.SerializeAsJson(openApiSpecVersion)
            };

            Logger.LogDebug("  Writing OpenAPI spec to {FileName}...", fileName);
            await File.WriteAllTextAsync(fileName, docString);

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

    /// <summary>
    /// Allows derived plugins to post-process the OpenApiPathItem before it is added/merged into the document.
    /// </summary>
    /// <param name="pathItem">The OpenApiPathItem to process.</param>
    /// <param name="requestUri">The request URI.</param>
    /// <param name="parametrizedPath">The parametrized path string.</param>
    /// <returns>The processed OpenApiPathItem.</returns>
    protected virtual OpenApiPathItem ProcessPathItem(OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        // By default, return the path item unchanged. Derived plugins can override to add/modify path-level data.
        return pathItem;
    }

    /// <summary>
    /// Allows derived plugins to post-process the OpenApiDocument before it is serialized and written to disk.
    /// </summary>
    /// <param name="openApiDoc">The OpenApiDocument to process.</param>
    protected virtual void ProcessOpenApiDocument(OpenApiDocument openApiDoc)
    {
        // By default, do nothing. Derived plugins can override to add/modify document-level data.
    }

    private async Task<string> GetOperationIdAsync(string method, string serverUrl, string parametrizedPath)
    {
        ILanguageModelCompletionResponse? id = null;
        if (await languageModelClient.IsEnabledAsync())
        {
            id = await languageModelClient.GenerateChatCompletionAsync("api_operation_id", new()
            {
                { "request", $"{method.ToUpperInvariant()} {serverUrl}{parametrizedPath}" }
            });
        }
        return id?.Response ?? $"{method}{parametrizedPath.Replace('/', '.')}";
    }

    private async Task<string> GetOperationDescriptionAsync(string method, string serverUrl, string parametrizedPath)
    {
        ILanguageModelCompletionResponse? description = null;
        if (await languageModelClient.IsEnabledAsync())
        {
            description = await languageModelClient.GenerateChatCompletionAsync("api_operation_description", new()
            {
                { "request", $"{method.ToUpperInvariant()} {serverUrl}{parametrizedPath}" }
            });
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
        operation.RequestBody = new()
        {
            Content = new Dictionary<string, OpenApiMediaType>
            {
                {
                    GetMediaType(request.ContentType),
                    new()
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
            if (Http.StandardHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping standard header {HeaderName}", header.Name);
                continue;
            }

            if (Http.AuthHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping auth header {HeaderName}", header.Name);
                continue;
            }

            operation.Parameters.Add(new()
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = false,
                Schema = new() { Type = "string" }
            });
            Logger.LogDebug("    Added header {HeaderName}", header.Name);
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
#pragma warning disable IDE0004
        var dictionary = (queryParams.AllKeys as string[]).ToDictionary(k => k, k => queryParams[k] as object);
#pragma warning restore IDE0004

        foreach (var parameter in dictionary)
        {
            operation.Parameters.Add(new()
            {
                Name = parameter.Key,
                In = ParameterLocation.Query,
                Required = false,
                Schema = new() { Type = "string" }
            });
            Logger.LogDebug("    Added query string parameter {ParameterKey}", parameter.Key);
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
        var responseCode = response.StatusCode.ToString(CultureInfo.InvariantCulture);
        if (response.HasBody)
        {
            Logger.LogDebug("    Response has body");

            openApiResponse.Content.Add(GetMediaType(response.ContentType), new()
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
                if (Http.StandardHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping standard header {HeaderName}", header.Name);
                    continue;
                }

                if (Http.AuthHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping auth header {HeaderName}", header.Name);
                    continue;
                }

                if (openApiResponse.Headers.ContainsKey(header.Name))
                {
                    Logger.LogDebug("    Header {HeaderName} already exists in response", header.Name);
                    continue;
                }

                openApiResponse.Headers.Add(header.Name, new()
                {
                    Schema = new() { Type = "string" }
                });
                Logger.LogDebug("    Added header {HeaderName}", header.Name);
            }
        }
        else
        {
            Logger.LogDebug("    Response doesn't have headers");
        }

        operation.Responses.Add(responseCode, openApiResponse);
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
            return GetSchemaFromJsonString(body);
        }

        return null;
    }

    private void AddOrMergePathItem(List<OpenApiDocument> openApiDocs, OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        var serverUrl = requestUri.GetLeftPart(UriPartial.Authority);
        var openApiDoc = openApiDocs.FirstOrDefault(d => d.Servers.Any(s => s.Url == serverUrl));

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
            // since we've just added the path, we're done
            return;
        }

        Logger.LogDebug("  Merging path {ParametrizedPath} into OpenAPI spec...", parametrizedPath);
        var operation = pathItem.Operations.First();
        AddOrMergeOperation(value, operation.Key, operation.Value);
    }

    private void AddOrMergeOperation(OpenApiPathItem pathItem, OperationType operationType, OpenApiOperation apiOperation)
    {
        if (!pathItem.Operations.TryGetValue(operationType, out var value))
        {
            Logger.LogDebug("    Adding operation {OperationType} to path...", operationType);

            pathItem.AddOperation(operationType, apiOperation);
            // since we've just added the operation, we're done
            return;
        }

        Logger.LogDebug("    Merging operation {OperationType} into path...", operationType);

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
                Logger.LogDebug("      Adding parameter {ParameterName} to operation...", parameter.Name);
                operation.Parameters.Add(parameter);
                continue;
            }

            Logger.LogDebug("      Merging parameter {ParameterName}...", parameter.Name);
            MergeSchema(parameter?.Schema, paramFromOperation.Schema);
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
                Logger.LogDebug("        Adding property {PropertyKey} to schema...", property.Key);
                target.Properties.Add(property);
                continue;
            }

            if (property.Value.Type != "object")
            {
                Logger.LogDebug("        Property already found but is not an object. Skipping...");
                continue;
            }

            Logger.LogDebug("        Merging property {PropertyKey}...", property.Key);
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
        _ = operation.RequestBody.Content.TryGetValue(requestBodyType, out var bodyFromOperation);

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
        _ = operation.Responses.TryGetValue(apiResponseStatusCode, out var responseFromOperation);

        if (responseFromOperation is null)
        {
            Logger.LogDebug("    Adding response {ApiResponseStatusCode} to operation...", apiResponseStatusCode);

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
        _ = responseFromOperation.Content.TryGetValue(apiResponseContentType, out var contentFromOperation);

        if (contentFromOperation is null)
        {
            Logger.LogDebug("    Adding response {ApiResponseContentType} to {ApiResponseStatusCode} to response...", apiResponseContentType, apiResponseStatusCode);

            responseFromOperation.Content.Add(apiResponse.Content.First());
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

                pathItem.Parameters.Add(new()
                {
                    Name = parameterName,
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new() { Type = "string" }
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
        var fileName = $"{uri.Host}-{DateTime.Now:yyyyMMddHHmmss}.{ext}";
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
                Type = "object"
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
