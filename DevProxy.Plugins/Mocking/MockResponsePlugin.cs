// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Behavior;
using DevProxy.Plugins.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocking;

public sealed class MockResponseConfiguration
{
    [JsonIgnore]
    public bool BlockUnmockedRequests { get; set; }
    public IEnumerable<MockResponse> Mocks { get; set; } = [];
    [JsonIgnore]
    public string MocksFile { get; set; } = "mocks.json";
    [JsonIgnore]
    public bool NoMocks { get; set; }
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v1.1.0/mockresponseplugin.mocksfile.schema.json";
}

public class MockResponsePlugin(
    HttpClient httpClient,
    ILogger<MockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<MockResponseConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _noMocksOptionName = "--no-mocks";
    private const string _mocksFileOptionName = "--mocks-file";

    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;
    // tracks the number of times a mock has been applied
    // used in combination with mocks that have an Nth property
    private readonly ConcurrentDictionary<string, int> _appliedMocks = [];

    private MockResponsesLoader? _loader;
    private Argument<IEnumerable<string>>? _httpResponseFilesArgument;
    private Option<string>? _httpResponseMocksFileNameOption;

    public override string Name => nameof(MockResponsePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        _loader = ActivatorUtilities.CreateInstance<MockResponsesLoader>(e.ServiceProvider, Configuration);
    }

    public override Option[] GetOptions()
    {
        var _noMocks = new Option<bool?>(_noMocksOptionName, "-n")
        {
            Description = "Disable loading mock requests",
            HelpName = "no-mocks"
        };

        var _mocksFile = new Option<string?>(_mocksFileOptionName)
        {
            Description = "Provide a file populated with mock responses",
            HelpName = "mocks-file"
        };

        return [_noMocks, _mocksFile];
    }

    public override Command[] GetCommands()
    {
        var mocksCommand = new Command("mocks", "Manage mock responses");
        var mocksFromHttpResponseCommand = new Command("from-http-responses", "Create a mock response from HTTP responses");
        _httpResponseFilesArgument = new Argument<IEnumerable<string>>("http-response-files")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "Glob pattern to the file(s) containing HTTP responses to create mock responses from",
        };
        mocksFromHttpResponseCommand.Add(_httpResponseFilesArgument);
        _httpResponseMocksFileNameOption = new Option<string>("--mocks-file")
        {
            HelpName = "mocks file",
            Arity = ArgumentArity.ExactlyOne,
            Description = "File to save the generated mock responses to",
            Required = true
        };
        mocksFromHttpResponseCommand.Add(_httpResponseMocksFileNameOption);
        mocksFromHttpResponseCommand.SetAction(GenerateMocksFromHttpResponsesAsync);

        mocksCommand.AddCommands(new[]
        {
            mocksFromHttpResponseCommand
        }.OrderByName());
        return [mocksCommand];
    }

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var parseResult = e.ParseResult;

        // allow disabling of mocks as a command line option
        var noMocks = parseResult.GetValueOrDefault<bool?>(_noMocksOptionName);
        if (noMocks.HasValue)
        {
            Configuration.NoMocks = noMocks.Value;
        }
        if (Configuration.NoMocks)
        {
            // mocks have been disabled. No need to continue
            return;
        }

        // update the name of the mocks file to load from if supplied
        var mocksFile = parseResult.GetValueOrDefault<string?>(_mocksFileOptionName);
        if (mocksFile is not null)
        {
            Configuration.MocksFile = mocksFile;
        }

        Configuration.MocksFile = ProxyUtils.GetFullPath(Configuration.MocksFile, _proxyConfiguration.ConfigFile);

        // load the responses from the configured mocks file
        _loader!.InitFileWatcherAsync(CancellationToken.None).GetAwaiter().GetResult();

        ValidateMocks();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.Session.HttpClient.Request;
        var state = e.ResponseState;
        if (Configuration.NoMocks)
        {
            Logger.LogRequest("Mocks disabled", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (!e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        var matchingResponse = GetMatchingMockResponse(request);
        if (matchingResponse is not null)
        {
            // we need to clone the response so that we're not modifying
            // the original that might be used in other requests
            var clonedResponse = (MockResponse)matchingResponse.Clone();
            ProcessMockResponseInternal(e, clonedResponse);
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }
        else if (Configuration.BlockUnmockedRequests)
        {
            ProcessMockResponseInternal(e, new()
            {
                Request = new()
                {
                    Url = request.Url,
                    Method = request.Method ?? ""
                },
                Response = new()
                {
                    StatusCode = 502,
                    Body = new GraphErrorResponseBody(new()
                    {
                        Code = "Bad Gateway",
                        Message = $"No mock response found for {request.Method} {request.Url}"
                    })
                }
            });
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }

        Logger.LogRequest("No matching mock response found", MessageType.Skipped, new(e.Session));

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    protected virtual void ProcessMockResponse(ref byte[] body, IList<MockResponseHeader> headers, ProxyRequestArgs e, MockResponse? matchingResponse)
    {
    }

    protected virtual void ProcessMockResponse(ref string? body, IList<MockResponseHeader> headers, ProxyRequestArgs e, MockResponse? matchingResponse)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        ProcessMockResponse(ref bytes, headers, e, matchingResponse);
        body = Encoding.UTF8.GetString(bytes);
    }

    private void ValidateMocks()
    {
        Logger.LogDebug("Validating mock responses");

        if (Configuration.NoMocks)
        {
            Logger.LogDebug("Mocks are disabled");
            return;
        }

        if (Configuration.Mocks is null ||
            !Configuration.Mocks.Any())
        {
            Logger.LogDebug("No mock responses defined");
            return;
        }

        var unmatchedMockUrls = new List<string>();

        foreach (var mock in Configuration.Mocks)
        {
            if (mock.Request is null)
            {
                Logger.LogDebug("Mock response is missing a request");
                continue;
            }

            if (string.IsNullOrEmpty(mock.Request.Url))
            {
                Logger.LogDebug("Mock response is missing a URL");
                continue;
            }

            if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, mock.Request.Url, true))
            {
                unmatchedMockUrls.Add(mock.Request.Url);
            }
        }

        if (unmatchedMockUrls.Count == 0)
        {
            return;
        }

        var suggestedWildcards = ProxyUtils.GetWildcardPatterns(unmatchedMockUrls.AsReadOnly());
        Logger.LogWarning(
            "The following URLs in {MocksFile} don't match any URL to watch: {UnmatchedMocks}. Add the following URLs to URLs to watch: {UrlsToWatch}",
            Configuration.MocksFile,
            string.Join(", ", unmatchedMockUrls),
            string.Join(", ", suggestedWildcards)
        );
    }

    private MockResponse? GetMatchingMockResponse(Request request)
    {
        if (Configuration.NoMocks ||
            Configuration.Mocks is null ||
            !Configuration.Mocks.Any())
        {
            return null;
        }

        var mockResponse = Configuration.Mocks.FirstOrDefault(mockResponse =>
        {
            if (mockResponse.Request is null)
            {
                return false;
            }

            if (mockResponse.Request.Method != request.Method)
            {
                return false;
            }

            if (mockResponse.Request.Url == request.Url &&
                HasMatchingBody(mockResponse, request) &&
                IsNthRequest(mockResponse))
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!mockResponse.Request.Url.Contains('*', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // turn mock URL with wildcard into a regex and match against the request URL
            return Regex.IsMatch(request.Url, ProxyUtils.PatternToRegex(mockResponse.Request.Url)) &&
                HasMatchingBody(mockResponse, request) &&
                IsNthRequest(mockResponse);
        });

        if (mockResponse is not null && mockResponse.Request is not null)
        {
            _ = _appliedMocks.AddOrUpdate(mockResponse.Request.Url, 1, (_, value) => ++value);
        }

        return mockResponse;
    }

    private bool IsNthRequest(MockResponse mockResponse)
    {
        if (mockResponse.Request?.Nth is null)
        {
            // mock doesn't define an Nth property so it always qualifies
            return true;
        }

        _ = _appliedMocks.TryGetValue(mockResponse.Request.Url, out var nth);
        nth++;

        return mockResponse.Request.Nth == nth;
    }

    private void ProcessMockResponseInternal(ProxyRequestArgs e, MockResponse matchingResponse)
    {
        string? body = null;
        var requestId = Guid.NewGuid().ToString();
        var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var headers = ProxyUtils.BuildGraphResponseHeaders(e.Session.HttpClient.Request, requestId, requestDate);
        var statusCode = HttpStatusCode.OK;
        if (matchingResponse.Response?.StatusCode is not null)
        {
            statusCode = (HttpStatusCode)matchingResponse.Response.StatusCode;
        }

        if (matchingResponse.Response?.Headers is not null)
        {
            ProxyUtils.MergeHeaders(headers, [.. matchingResponse.Response.Headers]);
        }

        // default the content type to application/json unless set in the mock response
        if (!headers.Any(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) &&
            matchingResponse.Response?.Body is not null)
        {
            headers.Add(new("content-type", "application/json"));
        }

        if (e.SessionData.TryGetValue(nameof(RateLimitingPlugin), out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
        }

        ReplacePlaceholders(matchingResponse.Response, e.Session.HttpClient.Request, Logger);

        if (matchingResponse.Response?.Body is not null)
        {
            var bodyString = JsonSerializer.Serialize(matchingResponse.Response.Body, ProxyUtils.JsonSerializerOptions) as string;
            // we get a JSON string so need to start with the opening quote
            if (bodyString?.StartsWith("\"@", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                // we've got a mock body starting with @-token which means we're sending
                // a response from a file on disk
                // if we can read the file, we can immediately send the response and
                // skip the rest of the logic in this method
                // remove the surrounding quotes and the @-token
                var filePath = Path.Combine(Path.GetDirectoryName(Configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(bodyString.Trim('"')[1..]));
                if (!File.Exists(filePath))
                {

                    Logger.LogError("File {FilePath} not found. Serving file path in the mock response", filePath);
                    body = bodyString;
                }
                else
                {
                    var bodyBytes = File.ReadAllBytes(filePath);
                    ProcessMockResponse(ref bodyBytes, headers, e, matchingResponse);
                    e.Session.GenericResponse(bodyBytes, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));
                    Logger.LogRequest($"{matchingResponse.Response.StatusCode ?? 200} {matchingResponse.Request?.Url}", MessageType.Mocked, new LoggingContext(e.Session));
                    return;
                }
            }
            else
            {
                body = bodyString;
            }
        }
        else
        {
            // we need to remove the content-type header if the body is empty
            // some clients fail on empty body + content-type
            var contentTypeHeader = headers.FirstOrDefault(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase));
            if (contentTypeHeader is not null)
            {
                _ = headers.Remove(contentTypeHeader);
            }
        }
        ProcessMockResponse(ref body, headers, e, matchingResponse);
        e.Session.GenericResponse(body ?? string.Empty, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));

        Logger.LogRequest($"{matchingResponse.Response?.StatusCode ?? 200} {matchingResponse.Request?.Url}", MessageType.Mocked, new(e.Session));
    }

    private async Task GenerateMocksFromHttpResponsesAsync(ParseResult parseResult)
    {
        Logger.LogTrace("{Method} called", nameof(GenerateMocksFromHttpResponsesAsync));

        if (_httpResponseFilesArgument is null)
        {
            throw new InvalidOperationException("HTTP response files argument is not initialized.");
        }
        if (_httpResponseMocksFileNameOption is null)
        {
            throw new InvalidOperationException("HTTP response mocks file name option is not initialized.");
        }

        var outputFilePath = parseResult.GetValue(_httpResponseMocksFileNameOption);
        if (string.IsNullOrEmpty(outputFilePath))
        {
            Logger.LogError("No output file path provided for mock responses.");
            return;
        }

        var httpResponseFiles = parseResult.GetValue(_httpResponseFilesArgument);
        if (httpResponseFiles is null || !httpResponseFiles.Any())
        {
            Logger.LogError("No HTTP response files provided.");
            return;
        }

        var matcher = new Matcher();
        matcher.AddIncludePatterns(httpResponseFiles);

        var matchingFiles = matcher.GetResultsInFullPath(".");
        if (!matchingFiles.Any())
        {
            Logger.LogError("No matching HTTP response files found.");
            return;
        }

        Logger.LogInformation("Found {FileCount} matching HTTP response files", matchingFiles.Count());
        Logger.LogDebug("Matching files: {Files}", string.Join(", ", matchingFiles));

        var mockResponses = new List<MockResponse>();
        foreach (var file in matchingFiles)
        {
            Logger.LogInformation("Processing file: {File}", Path.GetRelativePath(".", file));
            try
            {
                mockResponses.Add(MockResponse.FromHttpResponse(await File.ReadAllTextAsync(file), Logger));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing file {File}", file);
                continue;
            }
        }

        var mocksFile = new MockResponseConfiguration
        {
            Mocks = mockResponses
        };
        await File.WriteAllTextAsync(
            outputFilePath,
            JsonSerializer.Serialize(mocksFile, ProxyUtils.JsonSerializerOptions)
        );

        Logger.LogInformation("Generated mock responses saved to {OutputFile}", outputFilePath);

        Logger.LogTrace("Left {Method}", nameof(GenerateMocksFromHttpResponsesAsync));
    }

    private static bool HasMatchingBody(MockResponse mockResponse, Request request)
    {
        if (request.Method == "GET")
        {
            // GET requests don't have a body so we can't match on it
            return true;
        }

        if (mockResponse.Request?.BodyFragment is null)
        {
            // no body fragment to match on
            return true;
        }

        if (!request.HasBody || string.IsNullOrEmpty(request.BodyString))
        {
            // mock defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        return request.BodyString.Contains(mockResponse.Request.BodyFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplacePlaceholders(MockResponseResponse? response, Request request, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(ReplacePlaceholders));

        if (response is null ||
            response.Body is null || request.BodyString is null)
        {
            logger.LogTrace("Body is empty. Skipping replacing placeholders");
            return;
        }

        try
        {
            var requestBody = JsonSerializer.Deserialize<JsonElement>(request.BodyString, ProxyUtils.JsonSerializerOptions);

            response.Body = ReplacePlaceholdersInObject(response.Body, requestBody, logger);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse request body as JSON");
            logger.LogWarning("Failed to parse request body as JSON. Placeholders in the mock response won't be replaced.");
        }

        logger.LogTrace("Left {Method}", nameof(ReplacePlaceholders));
    }

    private static object? ReplacePlaceholdersInObject(object? obj, JsonElement requestBody, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(ReplacePlaceholdersInObject));

        if (obj is null)
        {
            return null;
        }

        // Handle JsonElement (which is what we get from System.Text.Json)
        if (obj is JsonElement element)
        {
            return ReplacePlaceholdersInJsonElement(element, requestBody, logger);
        }

        // Handle string values - check for placeholders
        if (obj is string strValue)
        {
            return ReplacePlaceholderInString(strValue, requestBody, logger);
        }

        // For other types, convert to JsonElement and process
        var json = JsonSerializer.Serialize(obj);
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
        return ReplacePlaceholdersInJsonElement(jsonElement, requestBody, logger);
    }

    private static object? ReplacePlaceholdersInJsonElement(JsonElement element, JsonElement requestBody, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(ReplacePlaceholdersInJsonElement));

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var resultObj = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    resultObj[property.Name] = ReplacePlaceholdersInJsonElement(property.Value, requestBody, logger);
                }
                return resultObj;

            case JsonValueKind.Array:
                var resultArray = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    resultArray.Add(ReplacePlaceholdersInJsonElement(item, requestBody, logger));
                }
                return resultArray;
            case JsonValueKind.String:
                return ReplacePlaceholderInString(element.GetString() ?? "", requestBody, logger);
            case JsonValueKind.Number:
                return GetSafeNumber(element, logger);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return element.ToString();
        }
    }

#pragma warning disable CA1859
    // CA1859: This method must return object? because it may return different concrete types (string, int, bool, etc.) based on the JSON content.
    private static object? ReplacePlaceholderInString(string value, JsonElement requestBody, ILogger logger)
#pragma warning restore CA1859
    {
        logger.LogTrace("{Method} called", nameof(ReplacePlaceholderInString));

        logger.LogDebug("Processing value: {Value}", value);

        // Check if the value starts with @request.body.
        if (!value.StartsWith("@request.body.", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Value {Value} does not start with @request.body. Skipping", value);
            return value;
        }

        // Extract the property path after @request.body.
        var propertyPath = value["@request.body.".Length..];

        logger.LogDebug("Extracted property path: {PropertyPath}", propertyPath);

        return GetValueFromRequestBody(requestBody, propertyPath, logger);
    }

    private static object? GetValueFromRequestBody(JsonElement requestBody, string propertyPath, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(GetValueFromRequestBody));

        logger.LogDebug("Getting value for {PropertyPath}", propertyPath);

        try
        {
            // Split the property path by dots to handle nested properties
            var propertyNames = propertyPath.Split('.');
            return GetNestedValueFromJsonElement(requestBody, propertyNames, logger);
        }
        catch (Exception ex)
        {
            // If we can't get the property, return null
            logger.LogDebug(ex, "Failed to get value for {PropertyPath}. Returning null", propertyPath);
        }

        return null;
    }

    private static object? GetNestedValueFromJsonElement(JsonElement element, string[] propertyNames, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(GetNestedValueFromJsonElement));

        var current = element;

        // Navigate through the nested properties
        foreach (var propertyName in propertyNames)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                logger.LogDebug("Current JSON element is not an object. Cannot navigate to property {PropertyName}", propertyName);
                return null; // Can't navigate further if current element is not an object
            }

            if (!current.TryGetProperty(propertyName, out current))
            {
                logger.LogDebug("Property {PropertyName} not found in JSON. Returning null", propertyName);
                return null; // Property not found
            }
        }

        return ConvertJsonElementToObject(current, logger);
    }

    private static object? ConvertJsonElementToObject(JsonElement element, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(ConvertJsonElementToObject));

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => GetSafeNumber(element, logger),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // For complex objects/arrays, return the JsonElement itself
            // which can be serialized later
            JsonValueKind.Object or JsonValueKind.Array => element,
            _ => element.ToString(),
        };
    }

    // Attempts to safely extract a number from a JsonElement, falling back to double or string if necessary
    private static object? GetSafeNumber(JsonElement element, ILogger logger)
    {
        logger.LogTrace("{Method} called", nameof(GetSafeNumber));

        // Try to get as int
        if (element.TryGetInt32(out var intValue))
        {
            return intValue;
        }
        if (element.TryGetInt64(out var longValue))
        {
            return longValue;
        }
        if (element.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }
        if (element.TryGetDouble(out var doubleValue))
        {
            return doubleValue;
        }

        // Fallback: return as string to avoid exceptions
        return element.GetRawText();
    }
}
