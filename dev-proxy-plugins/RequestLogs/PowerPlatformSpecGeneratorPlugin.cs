// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using DevProxy.Abstractions;
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
using DevProxy.Abstractions.LanguageModel;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.OpenApi.Any;

namespace DevProxy.Plugins.RequestLogs;

public class PowerPlatformSpecGeneratorPluginReportItem
{
    public required string ServerUrl { get; init; }
    public required string FileName { get; init; }
}

public class PowerPlatformSpecGeneratorPluginReport : List<PowerPlatformSpecGeneratorPluginReportItem>
{
    public PowerPlatformSpecGeneratorPluginReport() : base() { }

    public PowerPlatformSpecGeneratorPluginReport(IEnumerable<PowerPlatformSpecGeneratorPluginReportItem> collection) : base(collection) { }
}

internal class PowerPlatformSpecGeneratorPluginConfiguration
{
    public bool IncludeOptionsRequests { get; set; } = false;

    public SpecFormat SpecFormat { get; set; } = SpecFormat.Json;

    public bool IncludeResponseHeaders { get; set; } = false;

    public ContactConfig? Contact { get; set; }

    public ConnectorMetadataConfig? ConnectorMetadata { get; set; }
}

public class ContactConfig
{
    public string Name { get; set; } = "Your Name";
    public string Url { get; set; } = "https://www.yourwebsite.com";
    public string Email { get; set; } = "your.email@yourdomain.com";
}

public class ConnectorMetadataConfig
{
    public string? Website { get; set; }
    public string? PrivacyPolicy { get; set; }
    public string? Categories { get; set; }
}

public class PowerPlatformSpecGeneratorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(PowerPlatformSpecGeneratorPlugin);
    private readonly PowerPlatformSpecGeneratorPluginConfiguration _configuration = new();
    public static readonly string GeneratedPowerPlatformSpecsKey = "GeneratedPowerPlatformSpecs";

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        Logger.LogInformation("Creating Power Platform spec from recorded requests...");

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
              request.Context.Session is null ||
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
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
                var pathItem = await GetOpenApiPathItem(request.Context.Session);
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
                operationInfo.Value.Summary = await GetOperationSummaryAsync(
                    operationInfo.Key.ToString(),
                    request.Context.Session.HttpClient.Request.RequestUri.GetLeftPart(UriPartial.Authority),
                    parametrizedPath
                );
                await AddOrMergePathItem(openApiDocs, pathItem, request.Context.Session.HttpClient.Request.RequestUri, parametrizedPath);
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

            var openApiSpecVersion = OpenApiSpecVersion.OpenApi2_0;

            var docString = _configuration.SpecFormat switch
            {
                SpecFormat.Json => openApiDoc.SerializeAsJson(openApiSpecVersion),
                SpecFormat.Yaml => openApiDoc.SerializeAsYaml(openApiSpecVersion),
                _ => openApiDoc.SerializeAsJson(openApiSpecVersion)
            };

            Logger.LogDebug("  Writing OpenAPI spec to {fileName}...", fileName);
            File.WriteAllText(fileName, docString);

            generatedOpenApiSpecs.Add(server.Url, fileName);

            Logger.LogInformation("Created Power Platform spec file {fileName}", fileName);
        }

        StoreReport(new PowerPlatformSpecGeneratorPluginReport(
            generatedOpenApiSpecs
            .Select(kvp => new PowerPlatformSpecGeneratorPluginReportItem
            {
                ServerUrl = kvp.Key,
                FileName = kvp.Value
            })), e);

        // store the generated OpenAPI specs in the global data
        // for use by other plugins
        e.GlobalData[GeneratedPowerPlatformSpecsKey] = generatedOpenApiSpecs;
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
        var prompt = @"**Prompt:**
        Generate an operation ID for an OpenAPI specification based on the HTTP method and URL provided. Follow these rules:
        - The operation ID should be in camelCase format.
        - Start with a verb that matches the HTTP method (e.g., `get`, `create`, `update`, `delete`).
        - Use descriptive words from the URL path.
        - Replace path parameters (e.g., `{userId}`) with relevant nouns in singular form (e.g., `User`).
        - Do not provide explanations or any other text; respond only with the operation ID.

        Example:
        **Request:** `GET https://api.contoso.com/books/{books-id}`
        getBook

        Example:
        **Request:** `GET https://api.contoso.com/books/{books-id}/authors`
        getBookAuthors

        Example:
        **Request:** `GET https://api.contoso.com/books/{books-id}/authors/{authors-id}`
        getBookAuthor

        Example:
        **Request:** `POST https://api.contoso.com/books/{books-id}/authors`
        addBookAuthor

        Now, generate the operation ID for the following:
        **Request:** `{request}`".Replace("{request}", $"{method.ToUpper()} {serverUrl}{parametrizedPath}");
        ILanguageModelCompletionResponse? id = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            id = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
        }
        return id?.Response?.Trim() ?? $"{method}{parametrizedPath.Replace('/', '.')}";
    }

    private async Task<string> GetOperationSummaryAsync(string method, string serverUrl, string parametrizedPath)
    {
        var prompt = $@"You're an expert in OpenAPI. 
        You help developers build great OpenAPI specs for use with LLMs. 
        For the specified request, generate a concise, one-sentence summary that adheres to the following rules:
        - Must exist and be written in English.
        - Must be a phrase and cannot not end with punctuation.
        - Must be free of grammatical and spelling errors.
        - Must be 80 characters or less.
        - Must contain only alphanumeric characters or parentheses.        
        - Must not include the words API, Connector, or any other Power Platform product names (for example, Power Apps).
        - Respond with just the summary.

        For example:
        - For a request such as `GET https://api.contoso.com/books/{{books-id}}`, return `Get a book by ID`
        - For a request such as `POST https://api.contoso.com/books`, return `Create a new book`

        Request: {method.ToUpper()} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            description = await Context.LanguageModelClient.GenerateCompletionAsync(prompt);
        }
        return description?.Response?.Trim() ?? $"{method} {parametrizedPath}";
    }

    private async Task<string> GetOperationDescriptionAsync(string method, string serverUrl, string parametrizedPath)
    {
        var prompt = $@"You're an expert in OpenAPI. 
        You help developers build great OpenAPI specs for use with LLMs. 
        For the specified request, generate a one-sentence description that ends in punctuation. 
        Respond with just the description. 
        For example, for a request such as `GET https://api.contoso.com/books/{{books-id}}` 
        // you return `Get a book by ID`. Request: {method.ToUpper()} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            description = await Context.LanguageModelClient.GenerateCompletionAsync(prompt);
        }
        return description?.Response?.Trim() ?? $"{method} {parametrizedPath}";
    }

    private async Task<string> GenerateParameterDescriptionAsync(string parameterName, ParameterLocation location)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following parameter metadata, generate a concise and descriptive summary for the parameter. 
        The description must adhere to the following rules:
        - Must exist and be written in English.
        - Must be a full, descriptive sentence, and end in punctuation.
        - Must be free of grammatical and spelling errors.
        - Must describe the purpose of the parameter and its role in the request.
        - Can't contain any Copilot Studio or other Power Platform product names (for example, Power Apps).

        Parameter Metadata:
        - Name: {parameterName}
        - Location: {location}

        Examples:
        - For a query parameter named 'filter', return: 'Specifies a filter to narrow results.'
        - For a path parameter named 'userId', return: 'Specifies the user ID to retrieve details.'

        Now, generate the description for this parameter.";

        ILanguageModelCompletionResponse? response = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default logic if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterDescription(parameterName, location);
    }

    private async Task<string> GenerateParameterSummaryAsync(string parameterName, ParameterLocation location)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following parameter metadata, generate a concise and descriptive summary for the parameter. 
        The summary must adhere to the following rules:
        - Must exist and be written in English.
        - Must be free of grammatical and spelling errors.
        - Must be 80 characters or less.
        - Must contain only alphanumeric characters or parentheses.
        - Can't contain any Copilot Studio or other Power Platform product names (for example, Power Apps).

        Parameter Metadata:
        - Name: {parameterName}
        - Location: {location}

        Examples:
        - For a query parameter named 'filter', return: 'Filter results by a specific value.'
        - For a path parameter named 'userId', return: 'The unique identifier for a user.'

        Now, generate the summary for this parameter.";

        ILanguageModelCompletionResponse? response = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to a default summary if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterSummary(parameterName, location);
    }

    private string GetFallbackParameterSummary(string parameterName, ParameterLocation location)
    {
        return location switch
        {
            ParameterLocation.Query => $"Filter results with '{parameterName}'.",
            ParameterLocation.Header => $"Provide context with '{parameterName}'.",
            ParameterLocation.Path => $"Identify resource with '{parameterName}'.",
            ParameterLocation.Cookie => $"Manage session with '{parameterName}'.",
            _ => $"Provide info with '{parameterName}'."
        };
    }

    private string GetFallbackParameterDescription(string parameterName, ParameterLocation location)
    {
        return location switch
        {
            ParameterLocation.Query => $"Specifies the query parameter '{parameterName}' used to filter or modify the request.",
            ParameterLocation.Header => $"Specifies the header parameter '{parameterName}' used to provide additional context or metadata.",
            ParameterLocation.Path => $"Specifies the path parameter '{parameterName}' required to identify a specific resource.",
            ParameterLocation.Cookie => $"Specifies the cookie parameter '{parameterName}' used for session or state management.",
            _ => $"Specifies the parameter '{parameterName}' used in the request."
        };
    }

    private async Task<string> GetOpenApiDescriptionAsync(string defaultDescription)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following OpenAPI document metadata, generate a concise and descriptive summary for the API. 
        Include the purpose of the API and the types of operations it supports. Respond with just the description.

        OpenAPI Metadata:
        - Description: {defaultDescription}

        Rules:
        Must exist and be written in English.
        Must be free of grammatical and spelling errors.
        Should describe concisely the main purpose and value offered by your connector.
        Must be longer than 30 characters and shorter than 500 characters.
        Can't contain any Copilot Studio or other Power Platform product names (for example, Power Apps).

        Example:
        If the API is for managing books, you might respond with: 
        'Allows users to manage books, including operations to create, retrieve, update, and delete book records.'

        Now, generate the description for this API.";

        ILanguageModelCompletionResponse? description = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            description = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        return description?.Response?.Trim() ?? defaultDescription;
    }

    private async Task<string> GetOpenApiTitleAsync(string defaultTitle)
    {
        var prompt = $@"
                You're an expert in OpenAPI and API documentation. Based on the following guidelines, generate a concise and descriptive title for the API. 
                The title must meet the following requirements:

                - Must exist and be written in English.
                - Must be unique and distinguishable from any existing connector and/or plugin title.
                - Should be the name of the product or organization.
                - Should follow existing naming patterns for certified connectors and/or plugins. For independent publishers, the connector name should follow the pattern: Connector Name (Independent Publisher).
                - Can't be longer than 30 characters.
                - Can't contain the words API, Connector, Copilot Studio, or any other Power Platform product names (for example, Power Apps).
                - Can't end in a nonalphanumeric character, including carriage return, new line, or blank space.

                Examples:
                - Good titles: Azure Sentinel, Office 365 Outlook
                - Poor titles: Azure Sentinel's Power Apps Connector, Office 365 Outlook API

                Now, generate a title for the following API:
                Default Title: {defaultTitle}";

        ILanguageModelCompletionResponse? title = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            title = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default title if the language model fails
        return title?.Response?.Trim() ?? defaultTitle;
    }

    private async Task<string> GetConnectorMetadataWebsiteUrlAsync(string defaultUrl)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following API metadata, determine the corporate website URL for the API. 
        If the corporate website URL cannot be determined, respond with the default URL provided.

        API Metadata:
        - Default URL: {defaultUrl}

        Rules you must follow:
        - Do not output any explanations or additional text.
        - The URL must be a valid, publicly accessible website.
        - The URL must not contain placeholders or invalid characters.
        - If no corporate website URL can be determined, return the default URL.

        Example:
        Default URL: https://example.com
        Response: https://example.com

        Now, determine the corporate website URL for this API.";

        ILanguageModelCompletionResponse? response = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default URL if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    }

    private async Task<string> GetConnectorMetadataPrivacyPolicyUrlAsync(string defaultUrl)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following API metadata, determine the privacy policy URL for the corporate website or API. 
        If the privacy policy URL cannot be determined, respond with the default URL provided.

        API Metadata:
        - Default URL: {defaultUrl}

        Rules you must follow:
        - Do not output any explanations or additional text.
        - The URL must be a valid, publicly accessible website.
        - The URL must not contain placeholders or invalid characters.
        - If no privacy policy URL can be determined, return the default URL.

        Example:
        Response: https://example.com/privacy

        Now, determine the privacy policy URL for this API.";

        ILanguageModelCompletionResponse? response = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default URL if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    }

    private async Task<string> GetConnectorMetadataCategoriesAsync(string serverUrl, string defaultCategories)
    {
        var allowedCategories = @"""AI"", ""Business Management"", ""Business Intelligence"", ""Collaboration"", ""Commerce"", ""Communication"", 
        ""Content and Files"", ""Finance"", ""Data"", ""Human Resources"", ""Internet of Things"", ""IT Operations"", 
        ""Lifestyle and Entertainment"", ""Marketing"", ""Productivity"", ""Sales and CRM"", ""Security"", 
        ""Social Media"", ""Website""";

        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Based on the following API metadata and the server URL, determine the most appropriate categories for the API from the allowed list of categories. 
        If you cannot determine appropriate categories, respond with 'None'.

        API Metadata:
        - Server URL: {serverUrl}
        - Allowed Categories: {allowedCategories}

        Rules you must follow:
        - Do not output any explanations or additional text.
        - The categories must be from the allowed list.
        - The categories must be relevant to the API's functionality and purpose.
        - The categories should be in a comma-separated format.
        - If you cannot determine appropriate categories, respond with 'None'.

        Example:
        Allowed Categories: AI, Data
        Response: Data

        Now, determine the categories for this API.";

        ILanguageModelCompletionResponse? response = null;

        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // If the response is 'None' or empty, return the default categories
        return !string.IsNullOrWhiteSpace(response?.Response) && response.Response.Trim() != "None"
            ? response.Response
            : defaultCategories;
    }

    /**
     * Creates an OpenAPI PathItem from an intercepted request and response pair.
     * @param session The intercepted session.
     */
    private async Task<OpenApiPathItem> GetOpenApiPathItem(SessionEventArgs session)
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
        await SetParametersFromQueryString(operation, HttpUtility.ParseQueryString(request.RequestUri.Query));
        await SetParametersFromRequestHeaders(operation, request.Headers);
        await SetRequestBody(operation, request);
        await SetResponseFromSession(operation, response);

        path.Operations.Add(method, operation);

        return path;
    }

    private async Task SetRequestBody(OpenApiOperation operation, Request request)
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
                        Schema = await GetSchemaFromBody(GetMediaType(request.ContentType), request.BodyString)
                    }
                }
            }
        };
    }

    private async Task SetParametersFromRequestHeaders(OpenApiOperation operation, HeaderCollection headers)
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
                Logger.LogDebug("    Skipping standard header {headerName}", header.Name);
                continue;
            }

            if (Http.AuthHeaders.Contains(lowerCaseHeaderName))
            {
                Logger.LogDebug("    Skipping auth header {headerName}", header.Name);
                continue;
            }

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header.Name,
                In = ParameterLocation.Header,
                Required = false,
                Schema = new OpenApiSchema { Type = "string" },
                Description = await GenerateParameterDescriptionAsync(header.Name, ParameterLocation.Header),
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                    { "x-ms-summary", new OpenApiString(await GenerateParameterSummaryAsync(header.Name, ParameterLocation.Header)) }
                }
            });
            Logger.LogDebug("    Added header {headerName}", header.Name);
        }
    }

    private async Task SetParametersFromQueryString(OpenApiOperation operation, NameValueCollection queryParams)
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
                Schema = new OpenApiSchema { Type = "string" },
                Description = await GenerateParameterDescriptionAsync(parameter.Key, ParameterLocation.Query),
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                    { "x-ms-summary", new OpenApiString(await GenerateParameterSummaryAsync(parameter.Key, ParameterLocation.Query)) }
                }
            });
            Logger.LogDebug("    Added query string parameter {parameterKey}", parameter.Key);
        }
    }

    private async Task SetResponseFromSession(OpenApiOperation operation, Response response)
    {
        if (response is null)
        {
            Logger.LogDebug("  No response to process");
            return;
        }

        Logger.LogDebug($"  Processing response code {response.StatusCode} for operation {operation}...");

        var responseCode = response.StatusCode.ToString();
        bool is2xx = response.StatusCode >= 200 && response.StatusCode < 300;

        // Find all 2xx codes already present, sorted numerically
        var existing2xxCodes = operation.Responses.Keys
            .Where(k => int.TryParse(k, out int code) && code >= 200 && code < 300)
            .Select(k => int.Parse(k))
            .OrderBy(k => k)
            .ToList();

        // Determine if this is the lowest 2xx code
        bool isLowest2xx = is2xx && (!existing2xxCodes.Any() || response.StatusCode < existing2xxCodes.First());

        var openApiResponse = new OpenApiResponse
        {
            Description = isLowest2xx ? "default" : response.StatusDescription
        };

        if (response.HasBody)
        {
            Logger.LogDebug("    Response has body");
            var mediaType = GetMediaType(response.ContentType);

            if (isLowest2xx)
            {
                // Only the lowest 2xx response gets a schema
                openApiResponse.Content.Add(mediaType, new OpenApiMediaType
                {
                    Schema = await GetSchemaFromBody(mediaType, response.BodyString)
                });
            }
            else
            {
                // All other responses: no schema
                openApiResponse.Content.Add(mediaType, new OpenApiMediaType());
            }
        }
        else
        {
            Logger.LogDebug("    Response doesn't have body");
        }

        // Check configuration before processing headers
        if (!_configuration.IncludeResponseHeaders)
        {
            Logger.LogDebug("    Skipping response headers because IncludeResponseHeaders is set to false");
        }
        else if (response.Headers is not null && response.Headers.Any())
        {
            Logger.LogDebug("    Response has headers");

            foreach (var header in response.Headers)
            {
                var lowerCaseHeaderName = header.Name.ToLowerInvariant();
                if (Http.StandardHeaders.Contains(lowerCaseHeaderName))
                {
                    Logger.LogDebug("    Skipping standard header {headerName}", header.Name);
                    continue;
                }

                if (Http.AuthHeaders.Contains(lowerCaseHeaderName))
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

    private async Task<OpenApiSchema?> GetSchemaFromBody(string? contentType, string body)
    {
        if (contentType is null)
        {
            Logger.LogDebug("  No content type to process");
            return null;
        }

        if (contentType.StartsWith("application/json"))
        {
            Logger.LogDebug("    Processing JSON body...");
            return await GetSchemaFromJsonString(body);
        }

        return null;
    }

    private async Task AddOrMergePathItem(IList<OpenApiDocument> openApiDocs, OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
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
                    Title = await GetOpenApiTitleAsync($"{serverUrl} API"),
                    Description = await GetOpenApiDescriptionAsync($"{serverUrl} API"),
                    Contact = new OpenApiContact
                    {
                        Name = _configuration.Contact?.Name ?? "Your Name",
                        Url = new Uri(_configuration.Contact?.Url ?? "https://www.yourwebsite.com"),
                        Email = _configuration.Contact?.Email ?? "your.email@yourdomain.com"
                    }
                },
                Servers =
                [
                    new OpenApiServer { Url = serverUrl }
                ],
                Paths = [],
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                   { "x-ms-connector-metadata", new OpenApiArray
                        {
                            new OpenApiObject
                            {
                                ["propertyName"] = new OpenApiString("Website"),
                                ["propertyValue"] = new OpenApiString(
                                    _configuration.ConnectorMetadata?.Website
                                    ?? await GetConnectorMetadataWebsiteUrlAsync(serverUrl))
                            },
                            new OpenApiObject
                            {
                                ["propertyName"] = new OpenApiString("Privacy policy"),
                                ["propertyValue"] = new OpenApiString(
                                    _configuration.ConnectorMetadata?.PrivacyPolicy
                                    ?? await GetConnectorMetadataPrivacyPolicyUrlAsync(serverUrl))
                            },
                            new OpenApiObject
                            {
                                ["propertyName"] = new OpenApiString("Categories"),
                                ["propertyValue"] = new OpenApiString(
                                    _configuration.ConnectorMetadata?.Categories
                                    ?? await GetConnectorMetadataCategoriesAsync(serverUrl, "Data"))
                            }
                        }
                    }
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

    private async Task<OpenApiSchema> GetSchemaFromJsonString(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            JsonElement root = doc.RootElement;
            var schema = await GetSchemaFromJsonElement(root);
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

    private async Task<string> GetResponsePropertyTitleAsync(string propertyName)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Given a property name, generate a concise, human-readable title for the property. 
        The title must:
        - Be in Title Case (capitalize the first letter of each word).
        - Be 2-5 words long.
        - Not include underscores, dashes, or technical jargon.
        - Not repeat the property name verbatim if it contains underscores or is not human-friendly.
        - Be clear, descriptive, and suitable for use as a 'title' in OpenAPI schema properties.

        Examples:
        Property Name: tenant_id
        Title: Tenant ID

        Property Name: event_type
        Title: Event Type

        Property Name: created_at
        Title: Created At

        Property Name: user_email_address
        Title: User Email Address

        Now, generate a title for this property:
        Property Name: {propertyName}
        Title:
        ";

        ILanguageModelCompletionResponse? response = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.3 });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyTitleFallback(propertyName);
    }

    // Fallback if LLM fails
    private static string GetResponsePropertyTitleFallback(string propertyName)
    {
        return string.Join(" ", propertyName
            .Replace("_", " ")
            .Replace("-", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
    }

    private async Task<string> GetResponsePropertyDescriptionAsync(string propertyName)
    {
        var prompt = $@"
        You're an expert in OpenAPI and API documentation. Given a property name, generate a concise, human-readable description for the property.
        The description must:
        - Be a full, descriptive sentence and end in punctuation.
        - Be written in English.
        - Be free of grammatical and spelling errors.
        - Clearly explain the purpose or meaning of the property.
        - Not repeat the property name verbatim if it contains underscores or is not human-friendly.
        - Be suitable for use as a 'description' in OpenAPI schema properties.
        - Only return the description, without any additional text or explanation.

        Examples:
        Property Name: tenant_id
        Description: The ID of the tenant this notification belongs to.

        Property Name: event_type
        Description: The type of the event.

        Property Name: created_at
        Description: The timestamp of when the event was generated.

        Property Name: user_email_address
        Description: The email address of the user who triggered the event.

        Now, generate a description for this property:
        Property Name: {propertyName}
        Description:
        ";

        ILanguageModelCompletionResponse? response = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            response = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.3 });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyDescriptionFallback(propertyName);
    }

    // Fallback if LLM fails
    private static string GetResponsePropertyDescriptionFallback(string propertyName)
    {
        // Simple fallback: "The value of {Property Name}."
        var title = string.Join(" ", propertyName
            .Replace("_", " ")
            .Replace("-", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
        return $"The value of {title}.";
    }

    private async Task<OpenApiSchema> GetSchemaFromJsonElement(JsonElement jsonElement, string? propertyName = null)
    {
        // Log the start of processing this element
        Logger.LogDebug("Processing JSON element{0}{1}", 
            propertyName != null ? $" for property '{propertyName}'" : string.Empty,
            $", ValueKind: {jsonElement.ValueKind}");

        var schema = new OpenApiSchema();
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.String:
                schema.Type = "string";
                schema.Title = await GetResponsePropertyTitleAsync(propertyName ?? string.Empty);
                Logger.LogDebug("  Set type 'string' for property '{propertyName}'", propertyName);
                break;
            case JsonValueKind.Number:
                schema.Type = "number";
                schema.Title = await GetResponsePropertyTitleAsync(propertyName ?? string.Empty);
                Logger.LogDebug("  Set type 'number' for property '{propertyName}'", propertyName);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                schema.Type = "boolean";
                schema.Title = await GetResponsePropertyTitleAsync(propertyName ?? string.Empty);
                Logger.LogDebug("  Set type 'boolean' for property '{propertyName}'", propertyName);
                break;
            case JsonValueKind.Object:
                schema.Type = "object";
                schema.Properties = new Dictionary<string, OpenApiSchema>();
                Logger.LogDebug("  Processing object properties for '{propertyName}'", propertyName);
                foreach (var prop in jsonElement.EnumerateObject())
                {
                    schema.Properties[prop.Name] = await GetSchemaFromJsonElement(prop.Value, prop.Name);
                }
                break;
            case JsonValueKind.Array:
                schema.Type = "array";
                Logger.LogDebug("  Processing array items for '{propertyName}'", propertyName);
                schema.Items = await GetSchemaFromJsonElement(jsonElement.EnumerateArray().FirstOrDefault(), propertyName);
                break;
            default:
                schema.Type = "object";
                Logger.LogDebug("  Set default type 'object' for property '{propertyName}'", propertyName);
                break;
        }
        schema.Description = await GetResponsePropertyDescriptionAsync(propertyName ?? string.Empty);
        Logger.LogDebug("  Set description for property '{propertyName}': {description}", propertyName, schema.Description);
        return schema;
    }
}
