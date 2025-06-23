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

public class PowerPlatformOpenApiSpecGeneratorPlugin : OpenApiSpecGeneratorPlugin
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
    }


    public override string Name => nameof(PowerPlatformOpenApiSpecGeneratorPlugin);

    protected override OpenApiPathItem ProcessPathItem(OpenApiPathItem pathItem, Uri requestUri, string parametrizedPath)
    {
        ArgumentNullException.ThrowIfNull(pathItem);
        ArgumentNullException.ThrowIfNull(requestUri);

        // // Generate and add connector metadata
        // var serverUrl = requestUri.GetLeftPart(UriPartial.Authority);
        // // Synchronously wait for the async method (not recommended for production, but matches signature)
        // var connectorMetadata = GenerateConnectorMetadataAsync(serverUrl).GetAwaiter().GetResult();
        // pathItem.Extensions["x-ms-connector-metadata"] = connectorMetadata;

        return pathItem;
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



    // private async Task<OpenApiArray> GenerateConnectorMetadataAsync(string serverUrl)
    // {
    //     var website = await GetConnectorMetadataWebsiteUrlAsync(serverUrl);
    //     var privacyPolicy = await GetConnectorMetadataPrivacyPolicyUrlAsync(serverUrl);
    //     var categories = await GetConnectorMetadataCategoriesAsync(serverUrl, "Data");

    //     var metadataArray = new OpenApiArray
    //     {
    //         new OpenApiObject
    //         {
    //             ["propertyName"] = new OpenApiString("Website"),
    //             ["propertyValue"] = new OpenApiString(website)
    //         },
    //         new OpenApiObject
    //         {
    //             ["propertyName"] = new OpenApiString("Privacy policy"),
    //             ["propertyValue"] = new OpenApiString(privacyPolicy)
    //         },
    //         new OpenApiObject
    //         {
    //             ["propertyName"] = new OpenApiString("Categories"),
    //             ["propertyValue"] = new OpenApiString(categories)
    //         }
    //     };
    //     return metadataArray;
    // }

    // private async Task<string> GetConnectorMetadataWebsiteUrlAsync(string defaultUrl)
    // {
    //     var prompt = $@"
    //     You're an expert in OpenAPI and API documentation. Based on the following API metadata, determine the corporate website URL for the API. 
    //     If the corporate website URL cannot be determined, respond with the default URL provided.

    //     API Metadata:
    //     - Default URL: {defaultUrl}

    //     Rules you must follow:
    //     - Do not output any explanations or additional text.
    //     - The URL must be a valid, publicly accessible website.
    //     - The URL must not contain placeholders or invalid characters.
    //     - If no corporate website URL can be determined, return the default URL.

    //     Example:
    //     Default URL: https://example.com
    //     Response: https://example.com

    //     Now, determine the corporate website URL for this API.";

    //     ILanguageModelCompletionResponse? response = null;

    //     if (await _languageModelClient.IsEnabledAsync())
    //     {
    //         response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
    //     }

    //     // Fallback to the default URL if the language model fails or returns no response
    //     return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    // }

    // private async Task<string> GetConnectorMetadataPrivacyPolicyUrlAsync(string defaultUrl)
    // {
    //     var prompt = $@"
    //     You're an expert in OpenAPI and API documentation. Based on the following API metadata, determine the privacy policy URL for the corporate website or API. 
    //     If the privacy policy URL cannot be determined, respond with the default URL provided.

    //     API Metadata:
    //     - Default URL: {defaultUrl}

    //     Rules you must follow:
    //     - Do not output any explanations or additional text.
    //     - The URL must be a valid, publicly accessible website.
    //     - The URL must not contain placeholders or invalid characters.
    //     - If no privacy policy URL can be determined, return the default URL.

    //     Example:
    //     Response: https://example.com/privacy

    //     Now, determine the privacy policy URL for this API.";

    //     ILanguageModelCompletionResponse? response = null;

    //     if (await _languageModelClient.IsEnabledAsync())
    //     {
    //         response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
    //     }

    //     // Fallback to the default URL if the language model fails or returns no response
    //     return !string.IsNullOrWhiteSpace(response?.Response) ? response.Response.Trim() : defaultUrl;
    // }

    // private async Task<string> GetConnectorMetadataCategoriesAsync(string serverUrl, string defaultCategories)
    // {
    //     var allowedCategories = @"""AI"", ""Business Management"", ""Business Intelligence"", ""Collaboration"", ""Commerce"", ""Communication"", 
    //     ""Content and Files"", ""Finance"", ""Data"", ""Human Resources"", ""Internet of Things"", ""IT Operations"", 
    //     ""Lifestyle and Entertainment"", ""Marketing"", ""Productivity"", ""Sales and CRM"", ""Security"", 
    //     ""Social Media"", ""Website""";

    //     var prompt = $@"
    //     You're an expert in OpenAPI and API documentation. Based on the following API metadata and the server URL, determine the most appropriate categories for the API from the allowed list of categories. 
    //     If you cannot determine appropriate categories, respond with 'None'.

    //     API Metadata:
    //     - Server URL: {serverUrl}
    //     - Allowed Categories: {allowedCategories}

    //     Rules you must follow:
    //     - Do not output any explanations or additional text.
    //     - The categories must be from the allowed list.
    //     - The categories must be relevant to the API's functionality and purpose.
    //     - The categories should be in a comma-separated format.
    //     - If you cannot determine appropriate categories, respond with 'None'.

    //     Example:
    //     Allowed Categories: AI, Data
    //     Response: Data

    //     Now, determine the categories for this API.";

    //     ILanguageModelCompletionResponse? response = null;

    //     if (await _languageModelClient.IsEnabledAsync())
    //     {
    //         response = await _languageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 0.7 });
    //     }

    //     // If the response is 'None' or empty, return the default categories
    //     return !string.IsNullOrWhiteSpace(response?.Response) && response.Response.Trim() != "None"
    //         ? response.Response
    //         : defaultCategories;
    // }


}
