// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Mocking;

public sealed class MockStdioResponseConfiguration
{
    [JsonIgnore]
    public bool BlockUnmockedRequests { get; set; }

    public IEnumerable<MockStdioResponse> Mocks { get; set; } = [];

    [JsonIgnore]
    public string MocksFile { get; set; } = "stdio-mocks.json";

    [JsonIgnore]
    public bool NoMocks { get; set; }

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/mockstdioresponseplugin.mocksfile.schema.json";
}

public class MockStdioResponsePlugin(
    HttpClient httpClient,
    ILogger<MockStdioResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<MockStdioResponseConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _noMocksOptionName = "--no-stdio-mocks";
    private const string _mocksFileOptionName = "--stdio-mocks-file";

    private readonly IProxyConfiguration _proxyConfiguration = proxyConfiguration;

    // tracks the number of times a mock has been applied
    // used in combination with mocks that have an Nth property
    private readonly ConcurrentDictionary<string, int> _appliedMocks = [];

    // tracks whether we've applied startup mocks (mocks without stdin patterns)
    private bool _startupMocksApplied;

    private MockStdioResponsesLoader? _loader;

    public override string Name => nameof(MockStdioResponsePlugin);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        _loader = ActivatorUtilities.CreateInstance<MockStdioResponsesLoader>(e.ServiceProvider, Configuration);
    }

    public override Option[] GetOptions()
    {
        var noMocks = new Option<bool?>(_noMocksOptionName)
        {
            Description = "Disable loading stdio mock responses",
            HelpName = "no-stdio-mocks"
        };

        var mocksFile = new Option<string?>(_mocksFileOptionName)
        {
            Description = "Provide a file populated with stdio mock responses",
            HelpName = "stdio-mocks-file"
        };

        return [noMocks, mocksFile];
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
    }

    public override async Task BeforeStdinAsync(StdioRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeStdinAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (Configuration.NoMocks)
        {
            Logger.LogDebug("Stdio mocks disabled");
            return;
        }

        if (!e.ShouldExecute())
        {
            Logger.LogDebug("Response already set by another plugin");
            return;
        }

        // Apply startup mocks (mocks without stdin patterns) on first stdin
        if (!_startupMocksApplied)
        {
            _startupMocksApplied = true;
            ApplyStartupMocks(e);
        }

        var stdinBody = e.BodyString;
        var matchingResponse = GetMatchingMockResponse(stdinBody);

        if (matchingResponse is not null)
        {
            // Clone the response to avoid modifying the original
            var clonedResponse = (MockStdioResponse)matchingResponse.Clone();
            ProcessMockResponse(e, clonedResponse);
            e.ResponseState.HasBeenSet = true;

            Logger.LogRequest(
                $"Mocked stdin: {TruncateForLog(stdinBody)}",
                MessageType.Mocked,
                new StdioLoggingContext(e.Session, StdioMessageDirection.Stdin));

            return;
        }

        if (Configuration.BlockUnmockedRequests)
        {
            // Block unmocked requests by setting ResponseState
            e.ResponseState.HasBeenSet = true;

            Logger.LogRequest(
                $"Blocked unmocked stdin: {TruncateForLog(stdinBody)}",
                MessageType.Failed,
                new StdioLoggingContext(e.Session, StdioMessageDirection.Stdin));

            return;
        }

        Logger.LogDebug("No matching stdio mock response found for: {Stdin}", TruncateForLog(stdinBody));

        Logger.LogTrace("Left {Name}", nameof(BeforeStdinAsync));
    }

    private void ApplyStartupMocks(StdioRequestArgs e)
    {
        if (Configuration.Mocks is null || !Configuration.Mocks.Any())
        {
            return;
        }

        // Find mocks without stdin patterns (startup mocks)
        var startupMocks = Configuration.Mocks
            .Where(m => string.IsNullOrEmpty(m.Request?.BodyFragment) &&
                        string.IsNullOrEmpty(m.Request?.BodyRegex))
            .ToList();

        foreach (var mock in startupMocks)
        {
            if (mock.Response is null)
            {
                continue;
            }

            // Check Nth condition
            var mockKey = "startup_mock";
            if (mock.Request?.Nth is not null)
            {
                _ = _appliedMocks.TryGetValue(mockKey, out var nth);
                nth++;
                if (mock.Request.Nth != nth)
                {
                    _ = _appliedMocks.AddOrUpdate(mockKey, 1, (_, v) => ++v);
                    continue;
                }
            }

            Logger.LogInformation("Applying startup mock response");
            SendMockResponse(e, mock);

            _ = _appliedMocks.AddOrUpdate(mockKey, 1, (_, v) => ++v);
        }
    }

    private MockStdioResponse? GetMatchingMockResponse(string stdinBody)
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
                // Mocks without request patterns are startup mocks, skip them here
                return false;
            }

            var hasBodyFragment = !string.IsNullOrEmpty(mockResponse.Request.BodyFragment);
            var hasBodyRegex = !string.IsNullOrEmpty(mockResponse.Request.BodyRegex);

            if (!hasBodyFragment && !hasBodyRegex)
            {
                // No body fragment or regex means startup mock, handled separately
                return false;
            }

            // Use bodyRegex if specified, otherwise use bodyFragment (either/or)
            if (hasBodyRegex)
            {
                try
                {
                    if (!Regex.IsMatch(stdinBody, mockResponse.Request.BodyRegex!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
                    {
                        return false;
                    }
                }
                catch (RegexMatchTimeoutException ex)
                {
                    Logger.LogError(ex, "Regex match timed out for body regex pattern '{Pattern}'. Treating as non-matching mock.", mockResponse.Request.BodyRegex);
                    return false;
                }
                catch (ArgumentException ex)
                {
                    Logger.LogError(ex, "Invalid body regex pattern '{Pattern}'. Treating as non-matching mock.", mockResponse.Request.BodyRegex);
                    return false;
                }
            }
            else if (hasBodyFragment)
            {
                if (!stdinBody.Contains(mockResponse.Request.BodyFragment!, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check Nth condition
            return IsNthRequest(mockResponse);
        });

        if (mockResponse?.Request is not null)
        {
            var mockKey = GetMockKey(mockResponse);
            _ = _appliedMocks.AddOrUpdate(mockKey, 1, (_, value) => ++value);
        }

        return mockResponse;
    }

    private bool IsNthRequest(MockStdioResponse mockResponse)
    {
        if (mockResponse.Request?.Nth is null)
        {
            // mock doesn't define an Nth property so it always qualifies
            return true;
        }

        var mockKey = GetMockKey(mockResponse);
        _ = _appliedMocks.TryGetValue(mockKey, out var nth);
        nth++;

        return mockResponse.Request.Nth == nth;
    }

    private static string GetMockKey(MockStdioResponse mockResponse)
    {
        return mockResponse.Request?.BodyRegex
            ?? mockResponse.Request?.BodyFragment
            ?? "default";
    }

    private void ProcessMockResponse(StdioRequestArgs e, MockStdioResponse matchingResponse)
    {
        // Replace placeholders in the response with values from stdin
        ReplacePlaceholders(matchingResponse.Response, e.BodyString);
        SendMockResponse(e, matchingResponse);
    }

    private void SendMockResponse(StdioRequestArgs e, MockStdioResponse mock)
    {
        if (mock.Response is null)
        {
            return;
        }

        // Send stdout if present
        if (mock.Response.Stdout is not null)
        {
            var stdout = GetMockContent(mock.Response.Stdout, "stdout");
            if (!string.IsNullOrEmpty(stdout))
            {
                // Set on request args - ProxySession will send this
                e.StdoutResponse = string.IsNullOrEmpty(e.StdoutResponse)
                    ? stdout
                    : e.StdoutResponse + stdout;
            }
        }

        // Send stderr if present
        if (mock.Response.Stderr is not null)
        {
            var stderr = GetMockContent(mock.Response.Stderr, "stderr");
            if (!string.IsNullOrEmpty(stderr))
            {
                e.StderrResponse = string.IsNullOrEmpty(e.StderrResponse)
                    ? stderr
                    : e.StderrResponse + stderr;
            }
        }
    }

    private string GetMockContent(object? content, string contentType)
    {
        if (content is null)
        {
            return string.Empty;
        }

        // Serialize to JSON string first
        var contentString = JsonSerializer.Serialize(content, ProxyUtils.JsonSerializerOptions);

        // Check if content references a file (starts with @)
        if (contentString.StartsWith("\"@", StringComparison.OrdinalIgnoreCase))
        {
            // Remove surrounding quotes and @ prefix
            var filePath = contentString.Trim('"')[1..];
            filePath = Path.Combine(Path.GetDirectoryName(Configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(filePath));

            if (!File.Exists(filePath))
            {
                Logger.LogError("File {FilePath} not found for {ContentType}. Serving file path in the mock response", filePath, contentType);
                return contentString;
            }

            return File.ReadAllText(filePath);
        }

        // Remove surrounding quotes if present (from JSON string serialization)
        if (contentString.StartsWith('"') && contentString.EndsWith('"'))
        {
            contentString = contentString[1..^1];
            // Unescape JSON string
            contentString = Regex.Unescape(contentString);
        }

        return contentString;
    }

    private static string TruncateForLog(string value, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove newlines for cleaner logging
        value = value.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

        return value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, maxLength), "...");
    }

    private void ReplacePlaceholders(MockStdioResponseBody? response, string stdinBody)
    {
        Logger.LogTrace("{Method} called", nameof(ReplacePlaceholders));

        if (response is null)
        {
            Logger.LogTrace("Response is null. Skipping replacing placeholders");
            return;
        }

        JsonElement stdinJson = default;
        var hasValidJson = false;

        try
        {
            stdinJson = JsonSerializer.Deserialize<JsonElement>(stdinBody, ProxyUtils.JsonSerializerOptions);
            hasValidJson = true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to parse stdin as JSON");
            Logger.LogDebug("Placeholders in the mock response won't be replaced");
        }

        if (!hasValidJson)
        {
            return;
        }

        if (response.Stdout is not null)
        {
            response.Stdout = ReplacePlaceholdersInObject(response.Stdout, stdinJson);
        }

        if (response.Stderr is not null)
        {
            response.Stderr = ReplacePlaceholdersInObject(response.Stderr, stdinJson);
        }

        Logger.LogTrace("Left {Method}", nameof(ReplacePlaceholders));
    }

    private object? ReplacePlaceholdersInObject(object? obj, JsonElement stdinBody)
    {
        Logger.LogTrace("{Method} called", nameof(ReplacePlaceholdersInObject));

        if (obj is null)
        {
            return null;
        }

        // Handle JsonElement (which is what we get from System.Text.Json)
        if (obj is JsonElement element)
        {
            return ReplacePlaceholdersInJsonElement(element, stdinBody);
        }

        // Handle string values - check for placeholders
        if (obj is string strValue)
        {
            return ReplacePlaceholderInString(strValue, stdinBody);
        }

        // For other types, convert to JsonElement and process
        var json = JsonSerializer.Serialize(obj, ProxyUtils.JsonSerializerOptions);
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(json, ProxyUtils.JsonSerializerOptions);
        return ReplacePlaceholdersInJsonElement(jsonElement, stdinBody);
    }

    private object? ReplacePlaceholdersInJsonElement(JsonElement element, JsonElement stdinBody)
    {
        Logger.LogTrace("{Method} called", nameof(ReplacePlaceholdersInJsonElement));

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var resultObj = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    resultObj[property.Name] = ReplacePlaceholdersInJsonElement(property.Value, stdinBody);
                }
                return resultObj;

            case JsonValueKind.Array:
                var resultArray = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    resultArray.Add(ReplacePlaceholdersInJsonElement(item, stdinBody));
                }
                return resultArray;
            case JsonValueKind.String:
                return ReplacePlaceholderInString(element.GetString() ?? "", stdinBody);
            case JsonValueKind.Number:
                return GetSafeNumber(element);
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
    private object? ReplacePlaceholderInString(string value, JsonElement stdinBody)
#pragma warning restore CA1859
    {
        Logger.LogTrace("{Method} called", nameof(ReplacePlaceholderInString));

        Logger.LogDebug("Processing value: {Value}", value);

        // Check if the value contains @stdin.body. placeholder(s)
        if (!value.Contains("@stdin.body.", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Value does not contain @stdin.body. placeholder. Skipping");
            return value;
        }

        // If the entire value is a single placeholder, return the typed value
        if (value.StartsWith("@stdin.body.", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains(' ', StringComparison.Ordinal) &&
            value.IndexOf("@stdin.body.", 1, StringComparison.OrdinalIgnoreCase) == -1)
        {
            var propertyPath = value["@stdin.body.".Length..];
            Logger.LogDebug("Single placeholder, extracting property path: {PropertyPath}", propertyPath);
            return GetValueFromStdinBody(stdinBody, propertyPath);
        }

        // Replace all @stdin.body.xxx placeholders in the string
        var result = Regex.Replace(value, @"@stdin\.body\.([a-zA-Z0-9_.]+)", match =>
        {
            var propertyPath = match.Groups[1].Value;
            Logger.LogDebug("Replacing placeholder for property path: {PropertyPath}", propertyPath);

            var replacement = GetValueFromStdinBody(stdinBody, propertyPath);
            if (replacement is null)
            {
                return "null";
            }

            // For simple types, return the string representation
            if (replacement is string strReplacement)
            {
                return strReplacement;
            }

            // For other types (numbers, bools, objects), serialize to JSON
            return JsonSerializer.Serialize(replacement, ProxyUtils.JsonSerializerOptions);
        }, RegexOptions.IgnoreCase);

        Logger.LogDebug("Replaced value: {Value}", result);
        return result;
    }

    private object? GetValueFromStdinBody(JsonElement stdinBody, string propertyPath)
    {
        Logger.LogTrace("{Method} called", nameof(GetValueFromStdinBody));

        Logger.LogDebug("Getting value for {PropertyPath}", propertyPath);

        try
        {
            // Split the property path by dots to handle nested properties
            var propertyNames = propertyPath.Split('.');
            return GetNestedValueFromJsonElement(stdinBody, propertyNames);
        }
        catch (Exception ex)
        {
            // If we can't get the property, return null
            Logger.LogDebug(ex, "Failed to get value for {PropertyPath}. Returning null", propertyPath);
        }

        return null;
    }

    private object? GetNestedValueFromJsonElement(JsonElement element, string[] propertyNames)
    {
        Logger.LogTrace("{Method} called", nameof(GetNestedValueFromJsonElement));

        var current = element;

        // Navigate through the nested properties
        foreach (var propertyName in propertyNames)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                Logger.LogDebug("Current JSON element is not an object. Cannot navigate to property {PropertyName}", propertyName);
                return null; // Can't navigate further if current element is not an object
            }

            if (!current.TryGetProperty(propertyName, out current))
            {
                Logger.LogDebug("Property {PropertyName} not found in JSON. Returning null", propertyName);
                return null; // Property not found
            }
        }

        return ConvertJsonElementToObject(current);
    }

    private object? ConvertJsonElementToObject(JsonElement element)
    {
        Logger.LogTrace("{Method} called", nameof(ConvertJsonElementToObject));

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => GetSafeNumber(element),
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
    private object? GetSafeNumber(JsonElement element)
    {
        Logger.LogTrace("{Method} called", nameof(GetSafeNumber));

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
