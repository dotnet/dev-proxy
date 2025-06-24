using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using System.Globalization;

namespace DevProxy.Plugins.Generation;

public sealed class PowerPlatformOpenApiSpecGeneratorPlugin : OpenApiSpecGeneratorPlugin
{
    private readonly ILanguageModelClient _languageModelClient;

#pragma warning disable IDE0290 // Use primary constructor
    public PowerPlatformOpenApiSpecGeneratorPlugin(
#pragma warning restore IDE0290 // Use primary constructor
        ILogger<PowerPlatformOpenApiSpecGeneratorPlugin> logger,
        ISet<UrlToWatch> urlsToWatch,
        ILanguageModelClient languageModelClient,
        IProxyConfiguration proxyConfiguration,
        IConfigurationSection pluginConfigurationSection
    ) : base(logger, urlsToWatch, languageModelClient, proxyConfiguration, pluginConfigurationSection)
    {
        _languageModelClient = languageModelClient;
        Configuration.SpecVersion = SpecVersion.v2_0;
    }

    public override string Name => nameof(PowerPlatformOpenApiSpecGeneratorPlugin);

    /// <summary>
    /// Processes a single OpenAPI path item to set operation details, parameter descriptions, and response properties.
    /// This method is called synchronously during the OpenAPI document processing.
    /// </summary>
    /// <param name="pathItem">The OpenAPI path item to process.</param>
    /// <param name="requestUri">The request URI for context.</param>
    /// <param name="parametrizedPath">The parametrized path for the operation.</param>
    /// <returns>The processed OpenAPI path item.</returns>
    protected override OpenApiPathItem ProcessPathItem(OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        ArgumentNullException.ThrowIfNull(pathItem);
        ArgumentNullException.ThrowIfNull(requestUri);

        // Synchronously invoke the async details processor
        ProcessPathItemDetailsAsync(pathItem, requestUri, parametrizedPath).GetAwaiter().GetResult();
        return pathItem;
    }

    /// <summary>
    /// Processes the OpenAPI document to set contact information, title, description, and connector metadata.
    /// This method is called synchronously during the OpenAPI document processing.
    /// </summary>
    /// <param name="openApiDoc">The OpenAPI document to process.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="openApiDoc"/> is null.</exception>
    protected override void ProcessOpenApiDocument(OpenApiDocument openApiDoc)
    {
        ArgumentNullException.ThrowIfNull(openApiDoc);
        SetContactInfo(openApiDoc);
        SetTitleAndDescription(openApiDoc);

        // Try to get the server URL from the OpenAPI document
        var serverUrl = openApiDoc.Servers?.FirstOrDefault()?.Url;

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new InvalidOperationException("No server URL found in the OpenAPI document. Please ensure the document contains at least one server definition.");
        }

        // Synchronously call the async metadata generator
        var metadata = GenerateConnectorMetadataAsync(serverUrl).GetAwaiter().GetResult();
        openApiDoc.Extensions["x-ms-connector-metadata"] = metadata;
        RemoveConnectorMetadataExtension(openApiDoc);
    }

    /// <summary>
    /// Sets the OpenApi title and description in the Info area of the OpenApiDocument using LLM-generated values.
    /// </summary>
    /// <param name="openApiDoc">The OpenAPI document to process.</param>
    private void SetTitleAndDescription(OpenApiDocument openApiDoc)
    {
        // Synchronously call the async title/description generators
        var defaultTitle = openApiDoc.Info?.Title ?? "API";
        var defaultDescription = openApiDoc.Info?.Description ?? "API description.";
        var title = GetOpenApiTitleAsync(defaultTitle).GetAwaiter().GetResult();
        var description = GetOpenApiDescriptionAsync(defaultDescription).GetAwaiter().GetResult();
        openApiDoc.Info ??= new OpenApiInfo();
        openApiDoc.Info.Title = title;
        openApiDoc.Info.Description = description;
    }

    /// <summary>
    /// Sets the OpenApiContact in the Info area of the OpenApiDocument using configuration values.
    /// </summary>
    /// <param name="openApiDoc">The OpenAPI document to process.</param>
    private void SetContactInfo(OpenApiDocument openApiDoc)
    {
        openApiDoc.Info.Contact = new OpenApiContact
        {
            Name = Configuration.Contact?.Name ?? "Your Name",
            Url = Uri.TryCreate(Configuration.Contact?.Url, UriKind.Absolute, out var url) ? url : new Uri("https://www.yourwebsite.com"),
            Email = Configuration.Contact?.Email ?? "your.email@yourdomain.com"
        };
    }

    /// <summary>
    /// Removes the x-ms-connector-metadata extension from the OpenAPI document if it exists
    /// and is empty.
    /// </summary>
    /// <param name="openApiDoc">The OpenAPI document to process.</param>
    private async Task<string> GetOpenApiDescriptionAsync(string defaultDescription)
    {
        ILanguageModelCompletionResponse? description = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_description", new()
            {
                { "description", defaultDescription }
            });
        }

        return description?.Response?.Trim() ?? defaultDescription;
    }

    /// <summary>
    /// Generates a concise and descriptive title for the OpenAPI document using LLM or fallback logic.
    /// </summary>
    /// <param name="defaultTitle">The default title to use if LLM generation fails.</param>
    private async Task<string> GetOpenApiTitleAsync(string defaultTitle)
    {
        ILanguageModelCompletionResponse? title = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            title = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_title", new() {
                { "defaultTitle", defaultTitle }
            });
        }

        // Fallback to the default title if the language model fails
        return title?.Response?.Trim() ?? defaultTitle;
    }

    /// <summary>
    /// Processes all operations, parameters, and responses for a single OpenApiPathItem.
    /// </summary>
    /// <param name="pathItem">The OpenAPI path item to process.</param>
    /// <param name="requestUri">The request URI for context.</param>
    /// <param name="parametrizedPath">The parametrized path for the operation.</param>
    private async Task ProcessPathItemDetailsAsync(OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        var serverUrl = $"{requestUri.Scheme}://{requestUri.Host}{(requestUri.IsDefaultPort ? "" : ":" + requestUri.Port)}";
        foreach (var (method, operation) in pathItem.Operations)
        {
            // Update operationId
            operation.OperationId = await GetOperationIdAsync(method.ToString(), serverUrl, parametrizedPath);

            // Update summary
            operation.Summary = await GetOperationSummaryAsync(method.ToString(), serverUrl, parametrizedPath);

            // Update description
            operation.Description = await GetOperationDescriptionAsync(method.ToString(), serverUrl, parametrizedPath);

            // Combine operation-level and path-level parameters
            var allParameters = new List<OpenApiParameter>();
            allParameters.AddRange(operation.Parameters);
            allParameters.AddRange(pathItem.Parameters);

            foreach (var parameter in allParameters)
            {
                parameter.Description = await GenerateParameterDescriptionAsync(parameter.Name, parameter.In);
                parameter.Extensions["x-ms-summary"] = new OpenApiString(await GenerateParameterSummaryAsync(parameter.Name, parameter.In));
            }

            // Process responses
            if (operation.Responses != null)
            {
                foreach (var response in operation.Responses.Values)
                {
                    if (response.Content != null)
                    {
                        foreach (var mediaType in response.Content.Values)
                        {
                            if (mediaType.Schema != null)
                            {
                                await ProcessSchemaPropertiesAsync(mediaType.Schema);
                            }
                        }
                    }
                }
            }
        }
        RemoveResponseHeadersIfDisabled(pathItem);
    }

    /// <summary>
    /// Recursively processes all properties of an <see cref="OpenApiSchema"/>, setting their title and description using LLM or fallback logic.
    /// </summary>
    /// <param name="schema">The OpenAPI schema to process.</param>
    private async Task ProcessSchemaPropertiesAsync(OpenApiSchema schema)
    {
        if (schema.Properties != null)
        {
            foreach (var (propertyName, propertySchema) in schema.Properties)
            {
                propertySchema.Title = await GetResponsePropertyTitleAsync(propertyName);
                propertySchema.Description = await GetResponsePropertyDescriptionAsync(propertyName);
                // Recursively process nested schemas
                await ProcessSchemaPropertiesAsync(propertySchema);
            }
        }
        // Handle array items
        if (schema.Items != null)
        {
            await ProcessSchemaPropertiesAsync(schema.Items);
        }
    }

    /// <summary>
    /// Removes all response headers from the <see cref="OpenApiPathItem"/> if <c>IncludeResponseHeaders</c> is false in the configuration.
    /// </summary>
    /// <param name="pathItem">The OpenAPI path item to process.</param>
    private void RemoveResponseHeadersIfDisabled(OpenApiPathItem pathItem)
    {
        if (!Configuration.IncludeResponseHeaders && pathItem != null)
        {
            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.Responses != null)
                {
                    foreach (var response in operation.Responses.Values)
                    {
                        response.Headers?.Clear();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates an operationId for an OpenAPI operation using LLM or fallback logic.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="serverUrl">The server URL.</param>
    /// <param name="parametrizedPath">The parametrized path.</param>
    /// <returns>The generated operationId.</returns>
    private async Task<string> GetOperationIdAsync(string method, string serverUrl, string parametrizedPath)
    {
        ILanguageModelCompletionResponse? id = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            id = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_operation_id", new()
            {
                { "request", $"{method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}" }
            });
        }
        return id?.Response?.Trim() ?? $"{method}{parametrizedPath.Replace('/', '.')}";
    }

    /// <summary>
    /// Generates a summary for an OpenAPI operation using LLM or fallback logic.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="serverUrl">The server URL.</param>
    /// <param name="parametrizedPath">The parametrized path.</param>
    /// <returns>The generated summary.</returns>
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

        Request: {method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateCompletionAsync(prompt);
        }
        return description?.Response?.Trim() ?? $"{method} {parametrizedPath}";
    }

    /// <summary>
    /// Generates a description for an OpenAPI operation using LLM or fallback logic.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="serverUrl">The server URL.</param>
    /// <param name="parametrizedPath">The parametrized path.</param>
    /// <returns>The generated description.</returns>
    private async Task<string> GetOperationDescriptionAsync(string method, string serverUrl, string parametrizedPath)
    {
        ILanguageModelCompletionResponse? description = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_operation_description", new()
            {
                { "request", $"{method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}" }
            });
        }
        return description?.Response?.Trim() ?? $"{method} {parametrizedPath}";
    }

    /// <summary>
    /// Generates a description for an OpenAPI parameter using LLM or fallback logic.
    /// </summary>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <returns>The generated description.</returns>
    private async Task<string> GenerateParameterDescriptionAsync(string parameterName, ParameterLocation? location)
    {
        ILanguageModelCompletionResponse? response = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_parameter_description", new()
            {
                { "parameterName", parameterName },
                { "location", location?.ToString() ?? "unknown" }
            });
        }

        // Fallback to the default logic if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterDescription(parameterName, location);
    }

    /// <summary>
    /// Generates a summary for an OpenAPI parameter using LLM or fallback logic.
    /// </summary>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <returns>The generated summary.</returns>
    private async Task<string> GenerateParameterSummaryAsync(string parameterName, ParameterLocation? location)
    {
        ILanguageModelCompletionResponse? response = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_parameter_summary", new()
            {
                { "parameterName", parameterName },
                { "location", location?.ToString() ?? "unknown" }
            });
        }

        // Fallback to a default summary if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterSummary(parameterName, location);
    }

    /// <summary>
    /// Returns a fallback summary for a parameter if LLM generation fails.
    /// </summary>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <returns>The fallback summary string.</returns>
    private static string GetFallbackParameterSummary(string parameterName, ParameterLocation? location)
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

    /// <summary>
    /// Returns a fallback description for a parameter if LLM generation fails.
    /// </summary>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <returns>The fallback description string.</returns>
    private static string GetFallbackParameterDescription(string parameterName, ParameterLocation? location)
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

    /// <summary>
    /// Generates a title for a response property using LLM or fallback logic.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The generated title.</returns>
    private async Task<string> GetResponsePropertyTitleAsync(string propertyName)
    {
        ILanguageModelCompletionResponse? response = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_response_property_title", new()
            {
                { "propertyName", propertyName }
            });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyTitleFallback(propertyName);
    }

    /// <summary>
    /// Returns a fallback title for a response property if LLM generation fails.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The fallback title string.</returns>
    private static string GetResponsePropertyTitleFallback(string propertyName)
    {
        // Replace underscores and dashes with spaces, then ensure all lowercase before capitalizing
        var formattedPropertyName = propertyName
            .Replace("_", " ", StringComparison.InvariantCulture)
            .Replace("-", " ", StringComparison.InvariantCulture)
            .ToLowerInvariant();

        // Use TextInfo.ToTitleCase with InvariantCulture to capitalize each word
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        var title = textInfo.ToTitleCase(formattedPropertyName);

        return title;
    }

    /// <summary>
    /// Generates a description for a response property using LLM or fallback logic.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The generated description.</returns>
    private async Task<string> GetResponsePropertyDescriptionAsync(string propertyName)
    {
        ILanguageModelCompletionResponse? response = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_response_property_description", new()
            {
                { "propertyName", propertyName }
            });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyDescriptionFallback(propertyName);
    }

    /// <summary>
    /// Returns a fallback description for a response property if LLM generation fails.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The fallback description string.</returns>
    private static string GetResponsePropertyDescriptionFallback(string propertyName)
    {
        // Convert underscores and dashes to spaces, then ensure all lowercase before capitalizing
        var formattedPropertyName = propertyName
            .Replace("_", " ", StringComparison.InvariantCulture)
            .Replace("-", " ", StringComparison.InvariantCulture)
            .ToLowerInvariant();

        // Use TextInfo.ToTitleCase with InvariantCulture to capitalize each word
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        var description = textInfo.ToTitleCase(formattedPropertyName);

        // Construct the final description
        return $"The value of {description}.";
    }

    /// <summary>
    /// Generates the connector metadata OpenAPI extension array using configuration and LLM.
    /// </summary>
    /// <param name="serverUrl">The server URL for context.</param>
    /// <returns>An <see cref="OpenApiArray"/> containing connector metadata.</returns>
    private async Task<OpenApiArray> GenerateConnectorMetadataAsync(string serverUrl)
    {
        var website = Configuration.ConnectorMetadata?.Website ?? await GetConnectorMetadataWebsiteUrlAsync(serverUrl);
        var privacyPolicy = Configuration.ConnectorMetadata?.PrivacyPolicy ?? await GetConnectorMetadataPrivacyPolicyUrlAsync(serverUrl);

        string categories;
        var categoriesList = Configuration.ConnectorMetadata?.Categories;
        if (categoriesList != null && categoriesList.Count > 0)
        {
            categories = string.Join(", ", categoriesList);
        }
        else
        {
            categories = await GetConnectorMetadataCategoriesAsync(serverUrl, "Data");
        }

        var metadataArray = new OpenApiArray
        {
            new OpenApiObject
            {
                ["propertyName"] = new OpenApiString("Website"),
                ["propertyValue"] = new OpenApiString(website)
            },
            new OpenApiObject
            {
                ["propertyName"] = new OpenApiString("Privacy policy"),
                ["propertyValue"] = new OpenApiString(privacyPolicy)
            },
            new OpenApiObject
            {
                ["propertyName"] = new OpenApiString("Categories"),
                ["propertyValue"] = new OpenApiString(categories)
            }
        };
        return metadataArray;
    }

    /// <summary>
    /// Generates the website URL for connector metadata using LLM or configuration.
    /// </summary>
    /// <param name="defaultUrl">The default URL to use if LLM fails.</param>
    /// <returns>The website URL.</returns>
    private async Task<string> GetConnectorMetadataWebsiteUrlAsync(string defaultUrl)
    {
        ILanguageModelCompletionResponse? response = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_connector_metadata_website", new()
            {
                { "defaultUrl", defaultUrl }
            });
        }

        // Fallback to the default URL if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    }

    /// <summary>
    /// Generates the privacy policy URL for connector metadata using LLM or configuration.
    /// </summary>
    /// <param name="defaultUrl">The default URL to use if LLM fails.</param>
    /// <returns>The privacy policy URL.</returns>
    private async Task<string> GetConnectorMetadataPrivacyPolicyUrlAsync(string defaultUrl)
    {
        ILanguageModelCompletionResponse? response = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_connector_metadata_privacy_policy", new()
            {
                { "defaultUrl", defaultUrl }
            });
        }

        // Fallback to the default URL if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    }

    /// <summary>
    /// Generates the categories for connector metadata using LLM or configuration.
    /// </summary>
    /// <param name="serverUrl">The server URL for context.</param>
    /// <param name="defaultCategories">The default categories to use if LLM fails.</param>
    /// <returns>The categories string.</returns>
    private async Task<string> GetConnectorMetadataCategoriesAsync(string serverUrl, string defaultCategories)
    {
        ILanguageModelCompletionResponse? response = null;

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateChatCompletionAsync("powerplatform_api_connector_metadata_categories", new()
            {
                { "serverUrl", serverUrl }
            });
        }

        // If the response is 'None' or empty, return the default categories
        return !string.IsNullOrWhiteSpace(response?.Response) && response.Response.Trim() != "None"
            ? response.Response
            : defaultCategories;
    }

    /// <summary>
    /// Removes the x-ms-connector-metadata extension from the OpenAPI document if it exists.
    /// </summary>
    /// <param name="openApiDoc">The OpenAPI document to process.</param>
    private static void RemoveConnectorMetadataExtension(OpenApiDocument openApiDoc)
    {
        if (openApiDoc?.Extensions != null && openApiDoc.Extensions.ContainsKey("x-ms-generated-by"))
        {
            // Remove the x-ms-generated-by extension if it exists
            _ = openApiDoc.Extensions.Remove("x-ms-generated-by");
        }
    }

}
