// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Behavior;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevProxy.Plugins.Mocking;

public sealed class OpenApiMockResponsePluginConfiguration
{
    public string? SpecFile { get; set; }
}

public sealed class OpenApiMockResponsePlugin(
    HttpClient httpClient,
    ILogger<OpenApiMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<OpenApiMockResponsePluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string PromptFileName = "openapi_mock_response";

#pragma warning disable CA2213 // HttpClient is injected, so we don't dispose it
    private readonly HttpClient _httpClient = httpClient;
#pragma warning restore CA2213
    private readonly ILanguageModelClient _languageModelClient = languageModelClient;

    // the spec and the response bodies generated from it, cached per operation
    // (method + path template), are published together, so that requests
    // handled during a reload can't add stale bodies to the new cache
    private volatile SpecSnapshot? _snapshot;
    private OpenApiMockResponseSpecLoader? _loader;

    public override string Name => nameof(OpenApiMockResponsePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        if (string.IsNullOrWhiteSpace(Configuration.SpecFile))
        {
            Logger.LogError("No OpenAPI spec file specified. Set the specFile property in the {Plugin} configuration. The plugin will be disabled.", Name);
            Enabled = false;
            return;
        }

        if (IsRemoteSpec(Configuration.SpecFile))
        {
            await LoadRemoteSpecAsync(Configuration.SpecFile, cancellationToken);
        }
        else
        {
            Configuration.SpecFile = ProxyUtils.GetFullPath(Configuration.SpecFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<OpenApiMockResponseSpecLoader>(
                e.ServiceProvider,
                Configuration,
                (Action<string>)LoadSpec);
            await _loader.InitFileWatcherAsync(cancellationToken);
        }
    }

    public override async Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var snapshot = _snapshot;
        if (snapshot is null)
        {
            Logger.LogRequest("OpenAPI spec not loaded", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var request = e.ProxySession.Request;
        var pathItem = snapshot.Document.FindMatchingPathItem(request.RequestUri.AbsoluteUri, Logger);
        if (pathItem is null)
        {
            Logger.LogRequest("No matching path in the OpenAPI spec", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var (pathTemplate, pathItemValue) = pathItem.Value;
        var method = new HttpMethod(request.Method);
        if (pathItemValue.Operations?.TryGetValue(method, out var operation) != true || operation is null)
        {
            Logger.LogRequest("No matching operation in the OpenAPI spec", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var (statusCode, response) = GetMockResponse(operation);
        var (contentType, mediaType) = GetMediaType(response);
        if (mediaType is not null && contentType is not null && !CanGenerateBody(contentType, mediaType))
        {
            Logger.LogRequest($"Can't generate {contentType} response without an example in the OpenAPI spec", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        string? body = null;
        if (mediaType is not null && statusCode != HttpStatusCode.NoContent)
        {
            var cacheKey = $"{method.Method} {pathTemplate}";
            var lazyBody = snapshot.ResponseCache.GetOrAdd(cacheKey, _ => new(() =>
                // don't tie the cached generation to a single request's lifetime
                GenerateBodyAsync(method.Method, pathTemplate, operation, contentType!, mediaType, CancellationToken.None)));
            try
            {
                body = await lazyBody.Value;
            }
            catch (Exception ex)
            {
                // don't cache failures so that the next request retries
                _ = snapshot.ResponseCache.TryRemove(new(cacheKey, lazyBody));
                Logger.LogError(ex, "Failed to generate response for {Method} {Path}", method.Method, pathTemplate);
                return;
            }
        }

        var headers = new List<MockResponseHeader>();
        if (body is not null && contentType is not null)
        {
            headers.Add(new("Content-Type", contentType));
        }
        if (request.Headers.Any(h => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("Access-Control-Allow-Origin", "*"));
        }
        if (e.SessionData.TryGetValue(nameof(RateLimitingPlugin), out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
        }

        e.ProxySession.Respond(body ?? string.Empty, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));
        e.ResponseState.HasBeenSet = true;

        Logger.LogRequest($"{(int)statusCode} {pathTemplate}", MessageType.Mocked, new LoggingContext(e.ProxySession));

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    private static bool IsRemoteSpec(string specFile) =>
        Uri.TryCreate(specFile, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task LoadRemoteSpecAsync(string specUrl, CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogDebug("Downloading OpenAPI spec from {SpecUrl}...", specUrl);
            var contents = await _httpClient.GetStringAsync(new Uri(specUrl), cancellationToken);
            LoadSpec(contents);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to download OpenAPI spec from {SpecUrl}", specUrl);
        }
    }

    private void LoadSpec(string contents)
    {
        try
        {
            contents = ProxyUtils.ReplaceVariables(contents, ProxyConfiguration.Env, v => $"{{{v}}}");

            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();
            var readResult = OpenApiDocument.Parse(contents, null, settings);
            var document = readResult.Document;
            if (document is null)
            {
                Logger.LogError("Failed to parse OpenAPI spec {SpecFile}", Configuration.SpecFile);
                return;
            }

            if (document.Servers is null || document.Servers.Count == 0)
            {
                Logger.LogWarning("OpenAPI spec {SpecFile} doesn't define any servers. Requests can't be matched to its operations", Configuration.SpecFile);
            }

            PrioritizeConcretePaths(document);
            // the spec changed, so previously generated responses might be invalid
            _snapshot = new(document, new(StringComparer.Ordinal));

            Logger.LogInformation("Loaded OpenAPI spec {SpecFile} with {PathCount} paths", Configuration.SpecFile, document.Paths?.Count ?? 0);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load OpenAPI spec {SpecFile}", Configuration.SpecFile);
        }
    }

    // FindMatchingPathItem returns the first matching path, so put concrete
    // paths, eg. /users/me, before templated ones, eg. /users/{id}
    private static void PrioritizeConcretePaths(OpenApiDocument document)
    {
        if (document.Paths is null || document.Paths.Count == 0)
        {
            return;
        }

        var orderedPaths = new OpenApiPaths();
        foreach (var (path, pathItem) in document.Paths.OrderBy(p => p.Key.Count(c => c == '{')))
        {
            orderedPaths.Add(path, pathItem);
        }
        document.Paths = orderedPaths;
    }

    private static (HttpStatusCode StatusCode, IOpenApiResponse? Response) GetMockResponse(OpenApiOperation operation)
    {
        var responses = operation.Responses;
        if (responses is null || responses.Count == 0)
        {
            return (HttpStatusCode.OK, null);
        }

        var successResponse = responses
            .Where(r => r.Key.StartsWith('2'))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (successResponse.Value is not null)
        {
            var statusCode = int.TryParse(successResponse.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 200;
            return ((HttpStatusCode)statusCode, successResponse.Value);
        }

        return responses.TryGetValue("default", out var defaultResponse) ?
            (HttpStatusCode.OK, defaultResponse) :
            (HttpStatusCode.OK, null);
    }

    private static (string? ContentType, IOpenApiMediaType? MediaType) GetMediaType(IOpenApiResponse? response)
    {
        if (response?.Content is null || response.Content.Count == 0)
        {
            return (null, null);
        }

        var media = response.Content.FirstOrDefault(c => IsJsonContentType(c.Key));
        if (media.Value is null)
        {
            media = response.Content.First();
        }

        // wildcard content types aren't valid response content types
        var contentType = media.Key.Contains('*', StringComparison.Ordinal) ? "application/json" : media.Key;
        return (contentType, media.Value);
    }

    // JSON bodies can be generated from any schema. Text bodies only from
    // string schemas. For other media types, eg. XML, and text with non-string
    // schemas, only string examples from the spec can be used as-is
    private static bool CanGenerateBody(string contentType, IOpenApiMediaType mediaType)
    {
        if (IsJsonContentType(contentType))
        {
            return true;
        }

        var example = GetExample(mediaType);
        if (example is not null)
        {
            return example is JsonValue value && value.GetValueKind() == JsonValueKind.String;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            OpenApiSchemaSampleGenerator.IsStringSchema(mediaType.Schema);
    }

    private static bool IsJsonContentType(string contentType) =>
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains('*', StringComparison.Ordinal);

    private async Task<string?> GenerateBodyAsync(string method, string pathTemplate, OpenApiOperation operation, string contentType, IOpenApiMediaType mediaType, CancellationToken cancellationToken)
    {
        var isJson = IsJsonContentType(contentType);

        var example = GetExample(mediaType);
        if (example is not null)
        {
            Logger.LogDebug("Using example from the OpenAPI spec for {Method} {Path}", method, pathTemplate);
            return Serialize(example, isJson);
        }

        if (mediaType.Schema is null)
        {
            return null;
        }

        if (isJson)
        {
            var generated = await GenerateWithLanguageModelAsync(method, pathTemplate, operation, mediaType.Schema, cancellationToken);
            if (generated is not null)
            {
                return generated;
            }
        }

        Logger.LogDebug("Generating response for {Method} {Path} from schema", method, pathTemplate);
        var sample = OpenApiSchemaSampleGenerator.GenerateSample(mediaType.Schema);
        if (sample is null)
        {
            // schema allows only null, which is a valid JSON body
            return isJson ? "null" : null;
        }
        return Serialize(sample, isJson);
    }

    private static JsonNode? GetExample(IOpenApiMediaType mediaType)
    {
        if (mediaType.Example is not null)
        {
            return mediaType.Example;
        }

        return mediaType.Examples?.Values
            .Select(e => e?.Value)
            .FirstOrDefault(v => v is not null);
    }

    private static string Serialize(JsonNode node, bool isJson)
    {
        if (!isJson && node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        return node.ToJsonString(ProxyUtils.JsonSerializerOptions);
    }

    private async Task<string?> GenerateWithLanguageModelAsync(string method, string pathTemplate, OpenApiOperation operation, IOpenApiSchema schema, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _languageModelClient.IsEnabledAsync(cancellationToken))
            {
                return null;
            }

            var schemaDescription = OpenApiSchemaSampleGenerator.Describe(schema);
            var expectedType = schemaDescription["type"]?.GetValue<string>();
            if (expectedType == "null")
            {
                // the only valid value is null, so there's nothing to generate
                return null;
            }

            Logger.LogInformation("Generating response for {Method} {Path} using language model...", method, pathTemplate);

            var response = await _languageModelClient.GenerateChatCompletionAsync(PromptFileName, new()
            {
                { "request", $"{method} {pathTemplate}" },
                { "description", string.Join(" ", new[] { operation.Summary, operation.Description }.Where(s => !string.IsNullOrWhiteSpace(s))) },
                { "schema", schemaDescription.ToJsonString(ProxyUtils.JsonSerializerOptions) }
            }, cancellationToken);

            var json = NormalizeToExpectedType(ExtractJson(response?.Response), expectedType);
            if (json is null)
            {
                Logger.LogWarning("Language model didn't return valid JSON matching the schema for {Method} {Path}. Generating response from schema", method, pathTemplate);
                Logger.LogDebug("Language model response: {Response}", response?.Response);
                return null;
            }

            return json.ToJsonString(ProxyUtils.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to generate response for {Method} {Path} using language model. Generating response from schema", method, pathTemplate);
            return null;
        }
    }

    private static JsonNode? NormalizeToExpectedType(JsonNode? json, string? expectedType)
    {
        // small models often wrap scalar values in an object, eg. { "count": 42 }
        if (expectedType is "string" or "number" or "integer" or "boolean" &&
            json is JsonObject { Count: 1 } wrapper &&
            wrapper.First().Value is JsonValue wrappedValue)
        {
            json = wrappedValue.DeepClone();
        }

        switch (expectedType)
        {
            case "array" when json is JsonObject obj:
                // small models often wrap the array in an object, eg. { "users": [...] },
                // or return a single item instead of an array
                var arrays = obj.Where(p => p.Value is JsonArray).ToArray();
                if (arrays.Length == 1 && obj.Count == 1)
                {
                    return arrays[0].Value!.DeepClone();
                }
                return new JsonArray(obj.DeepClone());
            case "array" when json is not JsonArray:
            case "object" when json is not JsonObject:
                return null;
            case "string" when GetValueKind(json) != JsonValueKind.String:
            case "number" when GetValueKind(json) != JsonValueKind.Number:
            case "integer" when GetValueKind(json) != JsonValueKind.Number ||
                !json!.AsValue().TryGetValue<decimal>(out var integer) || integer != decimal.Truncate(integer):
            case "boolean" when GetValueKind(json) is not (JsonValueKind.True or JsonValueKind.False):
                return null;
            default:
                return json;
        }
    }

    private static JsonValueKind? GetValueKind(JsonNode? json) =>
        json is JsonValue value ? value.GetValueKind() : null;

    private static JsonNode? ExtractJson(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        // the response might be a complete JSON value, including scalars,
        // optionally wrapped in Markdown code fences
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal) && trimmed.Length > 6)
        {
            var firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
            trimmed = firstLineEnd < 0 ?
                trimmed[3..^3].Trim() :
                trimmed[(firstLineEnd + 1)..^3].Trim();
        }
        if (TryParseJson(trimmed, out var json))
        {
            return json;
        }

        // or JSON wrapped in other text, like markdown code fences
        var start = response.IndexOfAny(['{', '[']);
        var end = response.LastIndexOfAny(['}', ']']);
        if (start < 0 || end <= start)
        {
            return null;
        }

        return TryParseJson(response[start..(end + 1)], out json) ? json : null;
    }

    private static bool TryParseJson(string value, out JsonNode? json)
    {
        try
        {
            json = JsonNode.Parse(value);
            return json is not null;
        }
        catch (JsonException)
        {
            json = null;
            return false;
        }
    }

    private sealed record SpecSnapshot(
        OpenApiDocument Document,
        ConcurrentDictionary<string, Lazy<Task<string?>>> ResponseCache);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }
}
