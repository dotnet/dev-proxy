// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevProxy.Abstractions.Utils;

sealed class ParsedSample
{
    public string QueryVersion { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public string SampleUrl { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
}

public static class ProxyUtils
{
    private static readonly ConcurrentDictionary<string, JSchema> _schemaCache = new(StringComparer.Ordinal);
    private static readonly byte[] _utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly Regex itemPathRegex = new(@"(?:\/)[\w]+:[\w\/.]+(:(?=\/)|$)");
    private static readonly Regex sanitizedItemPathRegex = new("^[a-z]+:<value>$", RegexOptions.IgnoreCase);
    private static readonly Regex entityNameRegex = new("^((microsoft.graph(.[a-z]+)+)|[a-z]+)$", RegexOptions.IgnoreCase);
    // all alpha must include 2 to allow for oauth2PermissionScopes
    private static readonly Regex allAlphaRegex = new("^[a-z2]+$", RegexOptions.IgnoreCase);
    private static readonly Regex deprecationRegex = new("^[a-z]+_v2$", RegexOptions.IgnoreCase);
    private static readonly Regex functionCallRegex = new(@"^[a-z][a-z0-9_.]*\(.*\)$", RegexOptions.IgnoreCase);
    private static readonly TimeSpan remoteSchemaFallbackTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex devProxySchemaUrlRegex = new(
        @"^https://raw\.githubusercontent\.com/dotnet/dev-proxy/main/schemas/v(?<version>[^/]+)/(?<file>[A-Za-z0-9_.-]+\.json)$",
        RegexOptions.IgnoreCase);

    private static Assembly? _assembly;
    private static string _productVersion = string.Empty;

    public static readonly string ReportsKey = "Reports";

    // doesn't end with a path separator
    public static string? AppFolder => Path.GetDirectoryName(AppContext.BaseDirectory);

    /// <summary>
    /// Gets the path to the user data folder for Dev Proxy.
    /// On macOS: ~/Library/Application Support/dev-proxy/
    /// On Linux: ~/.config/dev-proxy/ (or $XDG_CONFIG_HOME/dev-proxy/)
    /// On Windows: %LocalAppData%\dev-proxy\
    /// </summary>
    public static string DataFolder
    {
        get
        {
            var basePath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(basePath, "dev-proxy");
        }
    }
    public static JsonSerializerOptions JsonSerializerOptions { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };
    public static JsonDocumentOptions JsonDocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };
    public static string ProductVersion
    {
        get
        {
            if (string.IsNullOrEmpty(_productVersion))
            {
                var assembly = GetAssembly();
                var assemblyVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

                if (assemblyVersionAttribute is null)
                {
                    _productVersion = assembly.GetName().Version?.ToString() ?? "";
                }
                else
                {
                    _productVersion = assemblyVersionAttribute.InformationalVersion;
                }
            }


            return _productVersion;
        }
    }

    static ProxyUtils()
    {
        // convert enum values to camelCase
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    /// <summary>
    /// Converts a configured response body to the string sent to the client.
    /// String bodies are returned as-is; other values are serialized as JSON.
    /// </summary>
    public static string SerializeBody(object? body) => body switch
    {
        null => string.Empty,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? string.Empty,
        JValue { Type: JTokenType.String } jv => jv.Value<string>() ?? string.Empty,
        _ => JsonSerializer.Serialize(body, JsonSerializerOptions)
    };

    public static bool IsGraphRequest(IHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsGraphUrl(request.RequestUri);
    }

    public static bool IsGraphUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.StartsWith("dod-graph.microsoft.", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.StartsWith("microsoftgraph.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGraphBatchUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.AbsoluteUri.EndsWith("/$batch", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri GetAbsoluteRequestUrlFromBatch(Uri batchRequestUri, string relativeRequestUrl)
    {
        ArgumentNullException.ThrowIfNull(batchRequestUri);

        var hostName = batchRequestUri.Host;
        var graphVersion = batchRequestUri.Segments[1].TrimEnd('/');
        var absoluteRequestUrl = new Uri($"https://{hostName}/{graphVersion}{relativeRequestUrl}");
        return absoluteRequestUrl;
    }

    public static bool IsSdkRequest(IHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Headers.Contains("SdkVersion");
    }

    public static bool IsGraphBetaRequest(IHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsGraphRequest(request) &&
            IsGraphBetaUrl(request.RequestUri);
    }

    public static bool IsGraphBetaUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.AbsolutePath.Contains("/beta/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Utility to build HTTP response headers consistent with Microsoft Graph
    /// </summary>
    /// <param name="request">The http request for which response headers are being constructed</param>
    /// <param name="requestId">string a guid representing the a unique identifier for the request</param>
    /// <param name="requestDate">string representation of the date and time the request was made</param>
    /// <returns>IList<MockResponseHeader> with defaults consistent with Microsoft Graph. Automatically adds CORS headers when the Origin header is present</returns>
    public static IList<MockResponseHeader> BuildGraphResponseHeaders(IHttpRequest request, string requestId, string requestDate)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsGraphRequest(request))
        {
            return [];
        }

        var hasOrigin = request.Headers.FirstOrDefault((h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is not null;
        return BuildGraphResponseHeadersCore(hasOrigin, requestId, requestDate);
    }

    private static List<MockResponseHeader> BuildGraphResponseHeadersCore(bool hasOriginHeader, string requestId, string requestDate)
    {
        var headers = new List<MockResponseHeader>
            {
                new ("Cache-Control", "no-store"),
                new ("x-ms-ags-diagnostic", ""),
                new ("Strict-Transport-Security", ""),
                new ("request-id", requestId),
                new ("client-request-id", requestId),
                new ("Date", requestDate),
                new ("Content-Type", "application/json")
            };
        if (hasOriginHeader)
        {
            headers.Add(new("Access-Control-Allow-Origin", "*"));
            headers.Add(new("Access-Control-Expose-Headers", "ETag, Location, Preference-Applied, Content-Range, request-id, client-request-id, ReadWriteConsistencyToken, SdkVersion, WWW-Authenticate, x-ms-client-gcc-tenant, Retry-After"));
        }
        return headers;
    }

    public static string ReplacePathTokens(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path ?? string.Empty;
        }

        path = ExpandHomeDirectory(path);
        path = path.Replace("~dataFolder", DataFolder, StringComparison.OrdinalIgnoreCase);
        return path.Replace("~appFolder", AppFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandHomeDirectory(string path)
    {
        if (path.Length < 2 || path[0] != '~' ||
            (path[1] != '/' && !(OperatingSystem.IsWindows() && path[1] == '\\')))
        {
            return path;
        }

        var homeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(homeFolder))
        {
            return path;
        }

        return Path.Join(homeFolder, path[2..]);
    }

    public static string GetFullPath(string? path, string? basePath) =>
        Path.GetFullPath(ReplacePathTokens(path), Path.GetDirectoryName(basePath) ?? string.Empty);

    // from: https://github.com/microsoftgraph/microsoft-graph-explorer-v4/blob/db86b903f36ef1b882996d46aee52cd49ed4444b/src/app/utils/query-url-sanitization.ts
#pragma warning disable CA1055
    public static string SanitizeUrl(string absoluteUrl)
#pragma warning restore CA1055
    {
        absoluteUrl = Uri.UnescapeDataString(absoluteUrl);
        var uri = new Uri(absoluteUrl);

        var parsedSample = ParseSampleUrl(absoluteUrl);
        var queryString = !string.IsNullOrEmpty(parsedSample.Search) ? $"?{SanitizeQueryParameters(parsedSample.Search)}" : "";

        // Sanitize item path specified in query url
        var resourceUrl = parsedSample.RequestUrl;
        if (!string.IsNullOrEmpty(resourceUrl))
        {
            resourceUrl = itemPathRegex.Replace(parsedSample.RequestUrl, match =>
            {
                return $"{match.Value[..match.Value.IndexOf(':', StringComparison.OrdinalIgnoreCase)]}:<value>";
            });
            // Split requestUrl into segments that can be sanitized individually
            var urlSegments = resourceUrl.Split('/');
            var sanitizedSegments = new string[urlSegments.Length];
            for (var i = 0; i < urlSegments.Length; i++)
            {
                sanitizedSegments[i] = SanitizePathSegment(i < 1 ? "" : urlSegments[i - 1], urlSegments[i]);
            }
            resourceUrl = string.Join('/', sanitizedSegments);
        }
        return $"{uri.GetLeftPart(UriPartial.Authority)}/{parsedSample.QueryVersion}/{resourceUrl}{queryString}";
    }

    /// <summary>
    /// Validates <paramref name="json"/> against the schema at <paramref name="schemaUrl"/>.
    /// Reports a schema that couldn't be retrieved as invalid. Use
    /// <see cref="ValidateJsonAgainstSchemaAsync(string?, string?, HttpClient, ILogger, CancellationToken)"/>
    /// to tell these cases apart.
    /// </summary>
    public static async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidateJsonAsync(string? json, string? schemaUrl, HttpClient httpClient, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ValidateJsonAgainstSchemaAsync(json, schemaUrl, httpClient, logger, cancellationToken);
            return (result.Status == SchemaValidationStatus.Valid, result.Errors);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error validating JSON");
            return (false, [ex.Message]);
        }
    }

    /// <summary>
    /// Validates <paramref name="json"/> against the schema at <paramref name="schemaUrl"/>.
    /// Schemas for the current Dev Proxy version are loaded from the schemas shipped with
    /// Dev Proxy, and only downloaded when the document doesn't match the local schema.
    /// </summary>
    /// <returns>
    /// The validation result. If the schema can't be loaded, downloaded or parsed, for example when
    /// there's no network connection, the status is <see cref="SchemaValidationStatus.SchemaUnavailable"/>
    /// so that callers can skip validation instead of treating the document as invalid.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<SchemaValidationResult> ValidateJsonAgainstSchemaAsync(string? json, string? schemaUrl, HttpClient httpClient, ILogger logger, CancellationToken cancellationToken) =>
        ValidateJsonAgainstSchemaAsync(json, schemaUrl, httpClient, logger, Path.Combine(AppFolder ?? string.Empty, "schemas"), NormalizeVersion(ProductVersion), remoteSchemaFallbackTimeout, cancellationToken);

    internal static Task<SchemaValidationResult> ValidateJsonAgainstSchemaAsync(string? json, string? schemaUrl, HttpClient httpClient, ILogger logger, string? localSchemasFolder, string currentVersion, TimeSpan remoteSchemaFallbackTimeout, CancellationToken cancellationToken)
    {
        logger.LogDebug("Validating JSON against schema {SchemaUrl}", schemaUrl);

        if (string.IsNullOrEmpty(json))
        {
            logger.LogDebug("JSON is empty, skipping validation");
            return Task.FromResult(new SchemaValidationResult(SchemaValidationStatus.Valid, []));
        }
        if (string.IsNullOrEmpty(schemaUrl))
        {
            logger.LogDebug("Schema URL is empty, skipping validation");
            return Task.FromResult(new SchemaValidationResult(SchemaValidationStatus.Valid, []));
        }

        return ValidateAgainstSchemaAsync(() => JToken.Parse(json), schemaUrl, httpClient, logger, localSchemasFolder, currentVersion, remoteSchemaFallbackTimeout, cancellationToken);
    }

    /// <summary>
    /// Returns the path of the schema shipped with Dev Proxy that corresponds
    /// to the given schema URL, or null if the URL doesn't point to a Dev Proxy
    /// schema for the current version or the schema isn't available locally.
    /// </summary>
    internal static string? GetLocalSchemaPath(string? schemaUrl, string? currentVersion, string? localSchemasFolder)
    {
        if (string.IsNullOrEmpty(schemaUrl) ||
            string.IsNullOrEmpty(currentVersion) ||
            string.IsNullOrEmpty(localSchemasFolder))
        {
            return null;
        }

        var match = devProxySchemaUrlRegex.Match(schemaUrl);
        if (!match.Success ||
            !string.Equals(match.Groups["version"].Value, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var localSchemaPath = Path.Combine(localSchemasFolder, match.Groups["file"].Value);
        return File.Exists(localSchemaPath) ? localSchemaPath : null;
    }

    private static async Task<SchemaValidationResult> ValidateAgainstSchemaAsync(Func<JToken> parseJson, string schemaUrl, HttpClient httpClient, ILogger logger, string? localSchemasFolder, string currentVersion, TimeSpan remoteSchemaFallbackTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // parse the JSON at most once, because a stream can only be read once
        JToken? token = null;
        Exception? parseError = null;
        SchemaValidationResult Validate(JSchema schema)
        {
            if (token is null && parseError is null)
            {
                try
                {
                    logger.LogDebug("Parsing JSON");
                    token = parseJson();
                }
                catch (Exception ex)
                {
                    parseError = ex;
                }
            }

            if (parseError is not null)
            {
                logger.LogDebug(parseError, "Error validating JSON");
                return new(SchemaValidationStatus.Invalid, [parseError.Message]);
            }

            try
            {
                logger.LogDebug("Validating JSON");
                var isValid = token!.IsValid(schema, out IList<string> errorMessages);
                return new(isValid ? SchemaValidationStatus.Valid : SchemaValidationStatus.Invalid, errorMessages);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error validating JSON");
                return new(SchemaValidationStatus.Invalid, [ex.Message]);
            }
        }

        var localSchema = await LoadLocalSchemaAsync(schemaUrl, logger, localSchemasFolder, currentVersion, cancellationToken);
        if (localSchema is not null)
        {
            var localResult = Validate(localSchema);
            // an updated schema can't fix JSON that can't be parsed
            if (localResult.Status == SchemaValidationStatus.Valid || parseError is not null)
            {
                return localResult;
            }

            // The schema might have been fixed after the release, so check
            // the latest version online before reporting the document as invalid
            JSchema remoteSchema;
            try
            {
                logger.LogDebug("JSON doesn't match the local schema. Checking the online schema for updates");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(remoteSchemaFallbackTimeout);
                remoteSchema = await GetSchemaAsync(schemaUrl, httpClient, logger, timeoutCts.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Couldn't retrieve schema {SchemaUrl}. Using the local schema validation result", schemaUrl);
                return localResult;
            }

            return Validate(remoteSchema);
        }

        JSchema schema;
        try
        {
            schema = await GetSchemaAsync(schemaUrl, httpClient, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error retrieving schema {SchemaUrl}", schemaUrl);
            return new(SchemaValidationStatus.SchemaUnavailable, [ex.Message]);
        }

        return Validate(schema);
    }

    private static async Task<JSchema?> LoadLocalSchemaAsync(string schemaUrl, ILogger logger, string? localSchemasFolder, string currentVersion, CancellationToken cancellationToken)
    {
        var localSchemaPath = GetLocalSchemaPath(schemaUrl, currentVersion, localSchemasFolder);
        if (localSchemaPath is null)
        {
            return null;
        }

        try
        {
            logger.LogDebug("Loading local schema {LocalSchemaPath} for {SchemaUrl}", localSchemaPath, schemaUrl);
            var schemaContents = await File.ReadAllTextAsync(localSchemaPath, cancellationToken);
            return JSchema.Parse(schemaContents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JSchemaReaderException or Newtonsoft.Json.JsonReaderException)
        {
            logger.LogDebug(ex, "Couldn't load local schema {LocalSchemaPath}", localSchemaPath);
            return null;
        }
    }

    /// <summary>
    /// Validates UTF-8 JSON read from <paramref name="json"/> against the schema at <paramref name="schemaUrl"/>.
    /// The stream is read from its current position and isn't disposed.
    /// Schemas for the current Dev Proxy version are loaded from the schemas shipped with
    /// Dev Proxy, and only downloaded when the document doesn't match the local schema.
    /// </summary>
    /// <returns>
    /// The validation result. If the schema can't be loaded, downloaded or parsed, for example when
    /// there's no network connection, the status is <see cref="SchemaValidationStatus.SchemaUnavailable"/>
    /// so that callers can skip validation instead of treating the document as invalid.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    internal static Task<SchemaValidationResult> ValidateJsonAgainstSchemaAsync(Stream json, string? schemaUrl, HttpClient httpClient, ILogger logger, CancellationToken cancellationToken) =>
        ValidateJsonAgainstSchemaAsync(json, schemaUrl, httpClient, logger, Path.Combine(AppFolder ?? string.Empty, "schemas"), NormalizeVersion(ProductVersion), remoteSchemaFallbackTimeout, cancellationToken);

    internal static Task<SchemaValidationResult> ValidateJsonAgainstSchemaAsync(Stream json, string? schemaUrl, HttpClient httpClient, ILogger logger, string? localSchemasFolder, string currentVersion, TimeSpan remoteSchemaFallbackTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogDebug("Validating JSON against schema {SchemaUrl}", schemaUrl);

        if (json.CanSeek && json.Length - json.Position == 0)
        {
            logger.LogDebug("JSON is empty, skipping validation");
            return Task.FromResult(new SchemaValidationResult(SchemaValidationStatus.Valid, []));
        }
        if (string.IsNullOrEmpty(schemaUrl))
        {
            logger.LogDebug("Schema URL is empty, skipping validation");
            return Task.FromResult(new SchemaValidationResult(SchemaValidationStatus.Valid, []));
        }

        return ValidateAgainstSchemaAsync(() => ParseJson(json), schemaUrl, httpClient, logger, localSchemasFolder, currentVersion, remoteSchemaFallbackTimeout, cancellationToken);
    }

    private static JToken ParseJson(Stream json)
    {
        // the JSON is read from a local or in-memory stream, where sync reads are cheaper than async ones
#pragma warning disable CA1849
        using var streamReader = new StreamReader(json, leaveOpen: true);
        using var jsonReader = new Newtonsoft.Json.JsonTextReader(streamReader);
        var token = JToken.Load(jsonReader);
        // same as JToken.Parse: fail on anything other than comments after the root value
        while (jsonReader.Read())
        {
        }
#pragma warning restore CA1849
        return token;
    }

    // Downloaded schemas are cached per process. Changes to a schema
    // published online are picked up the next time Dev Proxy starts.
    private static async Task<JSchema> GetSchemaAsync(string schemaUrl, HttpClient httpClient, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        cancellationToken.ThrowIfCancellationRequested();

        if (_schemaCache.TryGetValue(schemaUrl, out var cachedSchema))
        {
            logger.LogDebug("Using cached schema {SchemaUrl}", schemaUrl);
            return cachedSchema;
        }

        logger.LogDebug("Downloading schema from {SchemaUrl}", schemaUrl);
        var schemaContents = await httpClient.GetStringAsync(schemaUrl, cancellationToken);

        logger.LogDebug("Parsing schema");
        var schema = JSchema.Parse(schemaContents);
        return _schemaCache.GetOrAdd(schemaUrl, schema);
    }

    /// <summary>
    /// Reads the value of the top-level <c>$schema</c> property from UTF-8 JSON without
    /// parsing the whole document. Reading stops as soon as the property is found. If the
    /// document doesn't have the property, it's read to the end, which checks that it's valid JSON.
    /// The stream is read from its current position and isn't disposed.
    /// </summary>
    /// <returns>The schema URL, or <c>null</c> if the root isn't an object or doesn't have a <c>$schema</c> property.</returns>
    /// <exception cref="JsonException">The JSON is invalid before the <c>$schema</c> property is found.</exception>
    /// <exception cref="InvalidOperationException">The <c>$schema</c> property isn't a string.</exception>
    public static string? ReadSchemaReference(Stream utf8Json) =>
        ReadSchemaReference(utf8Json, readToEnd: false);

    /// <summary>
    /// Reads UTF-8 JSON to the end without materializing it, to check that it's valid JSON.
    /// The stream is read from its current position and isn't disposed.
    /// </summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    internal static void EnsureValidJson(Stream utf8Json) =>
        _ = ReadSchemaReference(utf8Json, readToEnd: true);

    private static string? ReadSchemaReference(Stream utf8Json, bool readToEnd)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = JsonDocumentOptions.AllowTrailingCommas,
                CommentHandling = JsonDocumentOptions.CommentHandling
            });

            var dataLength = utf8Json.ReadAtLeast(buffer, _utf8Bom.Length, throwOnEndOfStream: false);
            var isFinalBlock = dataLength < _utf8Bom.Length;
            if (buffer.AsSpan(0, dataLength).StartsWith(_utf8Bom))
            {
                buffer.AsSpan(_utf8Bom.Length, dataLength - _utf8Bom.Length).CopyTo(buffer);
                dataLength -= _utf8Bom.Length;
            }

            var isSchemaValue = false;
            string? schemaUrl = null;
            bool? isRootObject = null;
            while (true)
            {
                if (!isFinalBlock && dataLength < buffer.Length)
                {
                    var read = utf8Json.Read(buffer, dataLength, buffer.Length - dataLength);
                    isFinalBlock = read == 0;
                    dataLength += read;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLength), isFinalBlock, state);
                while (reader.Read())
                {
                    if (isSchemaValue)
                    {
                        isSchemaValue = false;
                        schemaUrl = reader.TokenType switch
                        {
                            JsonTokenType.String => reader.GetString(),
                            JsonTokenType.Null => null,
                            _ => throw new InvalidOperationException("The $schema property must be a string")
                        };
                        if (!readToEnd)
                        {
                            return schemaUrl;
                        }
                        continue;
                    }

                    // keep reading a root that isn't an object, so that invalid JSON is reported
                    isRootObject ??= reader.TokenType == JsonTokenType.StartObject;
                    if (isRootObject == true &&
                        reader.CurrentDepth == 1 &&
                        reader.TokenType == JsonTokenType.PropertyName &&
                        reader.ValueTextEquals("$schema"u8))
                    {
                        isSchemaValue = true;
                    }
                }

                if (isFinalBlock)
                {
                    return schemaUrl;
                }

                state = reader.CurrentState;
                var consumed = (int)reader.BytesConsumed;
                buffer.AsSpan(consumed, dataLength - consumed).CopyTo(buffer);
                dataLength -= consumed;

                // a single token doesn't fit in the buffer
                if (dataLength == buffer.Length)
                {
                    var largerBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, dataLength).CopyTo(largerBuffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = largerBuffer;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string ReplaceVariables(string s, Dictionary<string, string> variables, Func<string, string>? formatVariableRef = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(variables);

        formatVariableRef ??= v => $"@{v}";

        var s1 = s;

        foreach (var variable in variables)
        {
            s1 = s1.Replace(formatVariableRef(variable.Key), variable.Value, StringComparison.OrdinalIgnoreCase);
        }

        return s1;
    }

    public static void ValidateSchemaVersion(string schemaUrl, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(schemaUrl))
        {
            logger.LogDebug("Schema is empty, skipping schema version validation.");
            return;
        }

        var warning = GetSchemaVersionMismatchWarning(schemaUrl);
        if (warning is not null)
        {
            logger.LogWarning("{Warning}", warning);
        }
    }

    /// <summary>
    /// Checks if the schema URL version matches the current Dev Proxy version.
    /// Returns a warning message if versions don't match, or null if they match
    /// or the schema URL cannot be parsed.
    /// </summary>
    public static string? GetSchemaVersionMismatchWarning(string schemaUrl)
    {
        if (string.IsNullOrWhiteSpace(schemaUrl))
        {
            return null;
        }

        try
        {
            var uri = new Uri(schemaUrl);
            if (uri.Segments.Length > 2)
            {
                var schemaVersion = uri.Segments[^2]
                    .TrimStart('v')
                    .TrimEnd('/');
                var currentVersion = NormalizeVersion(ProductVersion);
                if (CompareSemVer(currentVersion, schemaVersion) != 0)
                {
                    var currentSchemaUrl = uri.ToString().Replace($"/v{schemaVersion}/", $"/v{currentVersion}/", StringComparison.OrdinalIgnoreCase);
                    return $"The version of schema does not match the installed Dev Proxy version, the expected schema is {currentSchemaUrl}";
                }
            }
        }
        catch
        {
            return $"The $schema value '{schemaUrl}' is not a valid URL. Schema version could not be validated.";
        }

        return null;
    }

    /// <summary>
    /// Returns the ordered list of config file paths to search.
    /// The first existing file in the list should be used.
    /// </summary>
    public static IEnumerable<string?> GetConfigFileCandidates(string? userConfigFile)
    {
        return [
            // config file specified by the user takes precedence
            // null if not specified
            userConfigFile,
            // current directory - JSON/JSONC files
            "devproxyrc.jsonc",
            "devproxyrc.json",
            // current directory - YAML files
            "devproxyrc.yaml",
            "devproxyrc.yml",
            // .devproxy subdirectory - JSON/JSONC files
            Path.Combine(".devproxy", "devproxyrc.jsonc"),
            Path.Combine(".devproxy", "devproxyrc.json"),
            // .devproxy subdirectory - YAML files
            Path.Combine(".devproxy", "devproxyrc.yaml"),
            Path.Combine(".devproxy", "devproxyrc.yml"),
            // app folder - JSON/JSONC files
            Path.Combine(AppFolder ?? "", "devproxyrc.jsonc"),
            Path.Combine(AppFolder ?? "", "devproxyrc.json"),
            // app folder - YAML files
            Path.Combine(AppFolder ?? "", "devproxyrc.yaml"),
            Path.Combine(AppFolder ?? "", "devproxyrc.yml")
        ];
    }

    /// <summary>
    /// Compares two semantic versions strings.
    /// </summary>
    /// <param name="a">ver1</param>
    /// <param name="b">ver2</param>
    /// <returns>
    /// Returns 0 if the versions are equal, -1 if a is less than b, and 1 if a is greater than b.
    /// An invalid argument is "rounded" to a minimal version.
    /// </returns>
    public static int CompareSemVer(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }
        else if (string.IsNullOrWhiteSpace(a))
        {
            return -1;
        }
        else if (string.IsNullOrWhiteSpace(b))
        {
            return 1;
        }

        a = a.TrimStart('v');
        b = b.TrimStart('v');

        var aParts = a.Split('-');
        var bParts = b.Split('-');

        var aParsed = Version.TryParse(aParts[0], out var aVersion);
        var bParsed = Version.TryParse(bParts[0], out var bVersion);
        if (!aParsed && !bParsed)
        {
            return 0;
        }
        else if (!aParsed)
        {
            return -1;
        }
        else if (!bParsed)
        {
            return 1;
        }

        var compare = aVersion!.CompareTo(bVersion);
        if (compare != 0)
        {
            // if the versions are different, return the comparison result
            return compare;
        }

        // if the versions are the same, compare the prerelease tags
        if (aParts.Length == 1 && bParts.Length == 1)
        {
            // if both versions are stable, they are equal
            return 0;
        }
        else if (aParts.Length == 1)
        {
            // if a is stable and b is not, a is greater
            return 1;
        }
        else if (bParts.Length == 1)
        {
            // if b is stable and a is not, b is greater
            return -1;
        }
        else if (aParts[1] == bParts[1])
        {
            // if both versions are prerelease and the tags are the same, they are equal
            return 0;
        }
        else
        {
            // if both versions are prerelease, b is greater
            return -1;
        }
    }

    /// <summary>
    /// Produces major.minor.patch version dropping a pre-release suffix.
    /// </summary>
    /// <param name="version">A version looks like "0.28.1", "0.28.1-alpha", "0.28.10-beta.1", "0.28.10-rc.1", or "0.28.0-preview-1", etc.</param>
    public static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return version.Split('-', StringSplitOptions.None)[0];
    }

    public static string GetGraphVersion(string url)
    {
        var uri = new Uri(url);
        return uri.Segments[1].Replace("/", "", StringComparison.OrdinalIgnoreCase);
    }

    public static void MergeHeaders(IList<MockResponseHeader> allHeaders, IList<MockResponseHeader> headersToAdd)
    {
        ArgumentNullException.ThrowIfNull(allHeaders);
        ArgumentNullException.ThrowIfNull(headersToAdd);

        foreach (var header in headersToAdd)
        {
            var existingHeader = allHeaders.FirstOrDefault(h => h.Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase));
            if (existingHeader is not null)
            {
                if (header.Name.Equals("Access-Control-Expose-Headers", StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Access-Control-Allow-Headers", StringComparison.OrdinalIgnoreCase))
                {
                    var existingValues = existingHeader.Value.Split(',').Select(v => v.Trim());
                    var newValues = header.Value.Split(',').Select(v => v.Trim());
                    var allValues = existingValues.Union(newValues).Distinct();
                    _ = allHeaders.Remove(existingHeader);
                    allHeaders.Add(new(header.Name, string.Join(", ", allValues)));
                    continue;
                }

                // don't remove headers that we've just added
                if (!headersToAdd.Contains(existingHeader))
                {
                    _ = allHeaders.Remove(existingHeader);
                }
            }

            allHeaders.Add(header);
        }
    }

    public static bool MatchesUrlToWatch(ISet<UrlToWatch> watchedUrls, string url, bool evaluateWildcards = false)
    {
        ArgumentNullException.ThrowIfNull(watchedUrls);
        ArgumentNullException.ThrowIfNull(url);

        if (evaluateWildcards && url.Contains('*', StringComparison.OrdinalIgnoreCase))
        {
            // url contains a wildcard, so convert it to regex and compare
            var match = watchedUrls.FirstOrDefault(r =>
            {
                var pattern = RegexToPattern(r.Url);
                var result = UrlRegexComparer.CompareRegexPatterns(pattern, url);
                return result != UrlRegexComparisonResult.PatternsMutuallyExclusive;
            });
            return match is not null && !match.Exclude;
        }
        else
        {
            var match = watchedUrls.FirstOrDefault(r => r.Url.IsMatch(url));
            return match is not null && !match.Exclude;
        }
    }

    public static string PatternToRegex(string pattern)
    {
        return $"^{Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase)}$";
    }

    /// <summary>
    /// Normalizes a WebSocket URL scheme to its HTTP equivalent (<c>wss://</c> →
    /// <c>https://</c>, <c>ws://</c> → <c>http://</c>), leaving any other scheme untouched.
    ///
    /// <para>
    /// The proxy engine reports an intercepted WebSocket upgrade with an <c>http(s)</c>
    /// scheme (a <c>ws(s)</c> connection is an HTTP <c>Upgrade</c> on the wire). Authors,
    /// however, naturally write <c>wss://host/*</c> in <c>urlsToWatch</c> and mock URLs.
    /// Normalizing both sides to <c>http(s)</c> before matching lets either form work.
    /// </para>
    /// </summary>
    public static string NormalizeWebSocketScheme(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("https://", url.AsSpan("wss://".Length));
        }
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("http://", url.AsSpan("ws://".Length));
        }
        return url;
    }

    public static string RegexToPattern(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);

        return Regex.Unescape(regex.ToString())
            .Trim('^', '$')
            .Replace(".*", "*", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> GetWildcardPatterns(ReadOnlyCollection<string> urls)
    {
        return [.. urls
            .GroupBy(url =>
            {
                if (url.Contains('*', StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }

                var uri = new Uri(url);
                return $"{uri.Scheme}://{uri.Host}";
            })
            .Select(group =>
            {
                if (group.Count() == 1)
                {
                    var url = group.First();
                    if (url.Contains('*', StringComparison.OrdinalIgnoreCase))
                    {
                        return url;
                    }

                    // For single URLs, use the URL up to the last segment
                    var uri = new Uri(url);
                    var path = uri.AbsolutePath;
                    var lastSlashIndex = path.LastIndexOf('/');
                    return $"{group.Key}{path[..lastSlashIndex]}/*";
                }

                // For multiple URLs, find the common prefix
                var paths = group.Select(url =>
                {
                    return url.Contains('*', StringComparison.OrdinalIgnoreCase) ? url : new Uri(url).AbsolutePath;
                }).ToList();
                var commonPrefix = GetCommonPrefix(paths);
                return $"{group.Key}{commonPrefix}*";
            })
            .OrderBy(x => x)];
    }

#pragma warning disable CA1055
    public static string UrlWithParametersToRegex(string urlWithParameters)
#pragma warning restore CA1055
    {
        ArgumentNullException.ThrowIfNull(urlWithParameters);

        return $"^{Regex.Replace(Regex.Escape(urlWithParameters), "\\\\{[^}]+}", ".*")}";
    }

    internal static Assembly GetAssembly()
            => _assembly ??= (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

    /**
    * Skipped segments:
    * - Entities, entity sets and navigation properties, expected to contain alphabetic letters only
    * - Deprecated entities in the form <entity>_v2
    * The remaining URL segments are assumed to be variables that need to be sanitized
    * @param segment
    */
    private static string SanitizePathSegment(string previousSegment, string segment)
    {
        var segmentsToIgnore = new[] { "$value", "$count", "$ref", "$batch" };

        if (IsAllAlpha(segment) ||
            IsDeprecation(segment) ||
            sanitizedItemPathRegex.IsMatch(segment) ||
            segmentsToIgnore.Contains(segment.ToLowerInvariant()) ||
            entityNameRegex.IsMatch(segment))
        {
            return segment;
        }

        // Check if segment is in this form: users('<some-id>|<UPN>') and transform to users(<value>)
        // or getOffice365ActiveUserDetail(period='D7') and transform to getOffice365ActiveUserDetail(period=<value>)
        if (IsFunctionCall(segment))
        {
            var openingBracketIndex = segment.IndexOf('(', StringComparison.Ordinal);
            var textWithinBrackets = segment[(openingBracketIndex + 1)..^1];
            var sanitizedText = string.IsNullOrWhiteSpace(textWithinBrackets)
                ? string.Empty
                : string.Join(',', SplitOutsideQuotes(textWithinBrackets, ',')
                    .Select(text =>
                    {
                        var keyValue = SplitOutsideQuotes(text, '=');
                        if (keyValue.Count > 1)
                        {
                            var key = keyValue[0].Trim();
                            key = !IsAllAlpha(key) ? "<key>" : key;
                            return $"{key}=<value>";
                        }
                        return "<value>";
                    }));

            return $"{segment[..openingBracketIndex]}({sanitizedText})";
        }

        if (IsPlaceHolderSegment(segment))
        {
            return segment;
        }

        if (!IsAllAlpha(previousSegment) && !IsDeprecation(previousSegment))
        {
            previousSegment = "unknown";
        }

        return $"{{{previousSegment}-id}}";
    }

    private static string SanitizeQueryParameters(string queryString)
    {
        // remove leading ? from query string and decode
        queryString = Uri.UnescapeDataString(
            new Regex(@"\+").Replace(queryString[1..], " ")
        );
        return string.Join('&', queryString.Split('&').Select(s => s));
    }

    private static bool IsAllAlpha(string value) => allAlphaRegex.IsMatch(value);

    private static bool IsDeprecation(string value) => deprecationRegex.IsMatch(value);

    private static bool IsFunctionCall(string value) => functionCallRegex.IsMatch(value);

    // Splits on the separator, ignoring separators inside OData single-quoted
    // string literals (where a quote is escaped by doubling it)
    private static List<string> SplitOutsideQuotes(string value, char separator)
    {
        var parts = new List<string>();
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (value[i] == separator && !inQuotes)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts;
    }

    private static bool IsPlaceHolderSegment(string segment)
    {
        return segment.StartsWith('{') && segment.EndsWith('}');
    }

    private static ParsedSample ParseSampleUrl(string url, string? version = null)
    {
        var parsedSample = new ParsedSample();

        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                url = RemoveExtraSlashesFromUrl(url);
                parsedSample.QueryVersion = version ?? GetGraphVersion(url);
                parsedSample.RequestUrl = GetRequestUrl(url, parsedSample.QueryVersion);
                parsedSample.Search = GenerateSearchParameters(url, "");
                parsedSample.SampleUrl = GenerateSampleUrl(url, parsedSample.QueryVersion, parsedSample.RequestUrl, parsedSample.Search);
            }
            catch (Exception) { }
        }

        return parsedSample;
    }

    private static string RemoveExtraSlashesFromUrl(string url)
    {
        return new Regex(@"([^:]\/)\/+").Replace(url, "$1");
    }

    private static string GetRequestUrl(string url, string version)
    {
        var uri = new Uri(url);
        var versionToReplace = uri.AbsolutePath.StartsWith($"/{version}", StringComparison.OrdinalIgnoreCase)
            ? version
            : GetGraphVersion(url);
        var requestContent = uri.AbsolutePath.Split(versionToReplace).LastOrDefault() ?? "";
        return Uri.UnescapeDataString(requestContent.TrimEnd('/')).TrimStart('/');
    }

    private static string GenerateSearchParameters(string url, string search)
    {
        var uri = new Uri(url);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            try
            {
                search = Uri.UnescapeDataString(uri.Query);
            }
            catch (Exception)
            {
                search = uri.Query;
            }
        }

        return new Regex(@"\s").Replace(search, "+");
    }

    private static string GenerateSampleUrl(
        string url,
        string queryVersion,
        string requestUrl,
        string search
    )
    {
        var uri = new Uri(url);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return RemoveExtraSlashesFromUrl($"{origin}/{queryVersion}/{requestUrl + search}");
    }

    private static string GetCommonPrefix(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        var firstPath = paths[0];
        var commonPrefixLength = firstPath.Length;

        for (var i = 1; i < paths.Count; i++)
        {
            commonPrefixLength = Math.Min(commonPrefixLength, paths[i].Length);
            for (var j = 0; j < commonPrefixLength; j++)
            {
                if (firstPath[j] != paths[i][j])
                {
                    commonPrefixLength = j;
                    break;
                }
            }
        }

        // Find the last complete path segment
        var prefix = firstPath[..commonPrefixLength];
        var lastSlashIndex = prefix.LastIndexOf('/');
        return lastSlashIndex >= 0 ? prefix[..(lastSlashIndex + 1)] : prefix;
    }
}
