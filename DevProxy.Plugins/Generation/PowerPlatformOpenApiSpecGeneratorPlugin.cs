using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using System.Globalization;


namespace DevProxy.Plugins.Generation;

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

public class PowerPlatformOpenApiSpecGeneratorPluginConfiguration : OpenApiSpecGeneratorPluginConfiguration
{
    public ContactConfig Contact { get; set; } = new();
    public ConnectorMetadataConfig ConnectorMetadata { get; set; } = new();
    public bool IncludeResponseHeaders { get; set; }
}

public class PowerPlatformOpenApiSpecGeneratorPlugin : OpenApiSpecGeneratorPlugin
{
    private readonly ILanguageModelClient _languageModelClient;
    private readonly PowerPlatformOpenApiSpecGeneratorPluginConfiguration _configuration;


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
        _configuration = pluginConfigurationSection.Get<PowerPlatformOpenApiSpecGeneratorPluginConfiguration>()
            ?? new();
        Configuration.SpecVersion = SpecVersion.v2_0;
    }


    public override string Name => nameof(PowerPlatformOpenApiSpecGeneratorPlugin);

    protected override OpenApiPathItem ProcessPathItem(OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        ArgumentNullException.ThrowIfNull(pathItem);
        ArgumentNullException.ThrowIfNull(requestUri);

        // Synchronously invoke the async details processor
        //ProcessPathItemDetailsAsync(pathItem, requestUri, parametrizedPath).GetAwaiter().GetResult();
        return pathItem;
    }

    protected override void ProcessOpenApiDocument(OpenApiDocument openApiDoc)
    {
        ArgumentNullException.ThrowIfNull(openApiDoc);
        SetContactInfo(openApiDoc);
        SetTitleAndDescription(openApiDoc);

        // Try to get the server URL from the OpenAPI document
        var serverUrl = openApiDoc.Servers?.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            // If no server URL, do not add metadata
            return;
        }

        foreach (var (path, pathItem) in openApiDoc.Paths)
        {
            // You can pass the path string if needed
            ProcessPathItemDetailsAsync(pathItem, new Uri(serverUrl), path).GetAwaiter().GetResult();
        }

        // Synchronously call the async metadata generator
        var metadata = GenerateConnectorMetadataAsync(serverUrl).GetAwaiter().GetResult();
        openApiDoc.Extensions["x-ms-connector-metadata"] = metadata;
        RemoveConnectorMetadataExtension(openApiDoc);
    }

    /// <summary>
    /// Sets the OpenApi title and description in the Info area of the OpenApiDocument using LLM-generated values.
    /// </summary>
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
    private void SetContactInfo(OpenApiDocument openApiDoc)
    {
        openApiDoc.Info.Contact = new OpenApiContact
        {
            Name = _configuration.Contact?.Name ?? "Your Name",
            Url = Uri.TryCreate(_configuration.Contact?.Url, UriKind.Absolute, out var url) ? url : new Uri("https://www.yourwebsite.com"),
            Email = _configuration.Contact?.Email ?? "your.email@yourdomain.com"
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            title = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default title if the language model fails
        return title?.Response?.Trim() ?? defaultTitle;
    }


    /// <summary>
    /// Processes all operations, parameters, and responses for a single OpenApiPathItem.
    /// </summary>
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
    /// Removes all response headers from the OpenApiPathItem if IncludeResponseHeaders is false.
    /// </summary>
    private void RemoveResponseHeadersIfDisabled(OpenApiPathItem pathItem)
    {
        if (!_configuration.IncludeResponseHeaders && pathItem != null)
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
        **Request:** `{request}`".Replace("{request}", $"{method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}", StringComparison.InvariantCulture);
        ILanguageModelCompletionResponse? id = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            id = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
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

        Request: {method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateCompletionAsync(prompt);
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
        // you return `Get a book by ID`. Request: {method.ToUpper(CultureInfo.InvariantCulture)} {serverUrl}{parametrizedPath}";
        ILanguageModelCompletionResponse? description = null;
        if (await _languageModelClient.IsEnabledAsync())
        {
            description = await _languageModelClient.GenerateCompletionAsync(prompt);
        }
        return description?.Response?.Trim() ?? $"{method} {parametrizedPath}";
    }

    private async Task<string> GenerateParameterDescriptionAsync(string parameterName, ParameterLocation? location)
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to the default logic if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterDescription(parameterName, location);
    }

    private async Task<string> GenerateParameterSummaryAsync(string parameterName, ParameterLocation? location)
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // Fallback to a default summary if the language model fails or returns no response
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetFallbackParameterSummary(parameterName, location);
    }

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
        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.3 });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyTitleFallback(propertyName);
    }

    // Fallback if LLM fails
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
        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.3 });
        }
        return !string.IsNullOrWhiteSpace(response?.Response)
            ? response.Response.Trim()
            : GetResponsePropertyDescriptionFallback(propertyName);
    }

    // Fallback if LLM fails
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

    private async Task<OpenApiArray> GenerateConnectorMetadataAsync(string serverUrl)
    {
        var website = _configuration.ConnectorMetadata?.Website ?? await GetConnectorMetadataWebsiteUrlAsync(serverUrl);
        var privacyPolicy = _configuration.ConnectorMetadata?.PrivacyPolicy ?? await GetConnectorMetadataPrivacyPolicyUrlAsync(serverUrl);
        var categories = _configuration.ConnectorMetadata?.Categories ?? await GetConnectorMetadataCategoriesAsync(serverUrl, "Data");

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

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
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

        if (await _languageModelClient.IsEnabledAsync())
        {
            response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
        }

        // If the response is 'None' or empty, return the default categories
        return !string.IsNullOrWhiteSpace(response?.Response) && response.Response.Trim() != "None"
            ? response.Response
            : defaultCategories;
    }

    /// <summary>
    /// Removes the x-ms-connector-metadata extension from the OpenAPI document if it exists.
    /// </summary>
    private static void RemoveConnectorMetadataExtension(OpenApiDocument openApiDoc)
    {
        if (openApiDoc?.Extensions != null && openApiDoc.Extensions.ContainsKey("x-ms-generated-by"))
        {
            // Remove the x-ms-generated-by extension if it exists
            _ = openApiDoc.Extensions.Remove("x-ms-generated-by");
        }
    }

}
