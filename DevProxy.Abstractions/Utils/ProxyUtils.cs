// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

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
    private static readonly Regex itemPathRegex = new(@"(?:\/)[\w]+:[\w\/.]+(:(?=\/)|$)");
    private static readonly Regex sanitizedItemPathRegex = new("^[a-z]+:<value>$", RegexOptions.IgnoreCase);
    private static readonly Regex entityNameRegex = new("^((microsoft.graph(.[a-z]+)+)|[a-z]+)$", RegexOptions.IgnoreCase);
    // all alpha must include 2 to allow for oauth2PermissionScopes
    private static readonly Regex allAlphaRegex = new("^[a-z2]+$", RegexOptions.IgnoreCase);
    private static readonly Regex deprecationRegex = new("^[a-z]+_v2$", RegexOptions.IgnoreCase);
    private static readonly Regex functionCallRegex = new(@"^[a-z]+\(.*\)$", RegexOptions.IgnoreCase);

    private static Assembly? _assembly;
    private static string _productVersion = string.Empty;

    public static readonly string ReportsKey = "Reports";

    // doesn't end with a path separator
    public static string? AppFolder => Path.GetDirectoryName(AppContext.BaseDirectory);
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

    public static bool IsGraphRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return IsGraphUrl(request.RequestUri);
    }

    public static bool IsGraphUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) ||
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

    public static bool IsSdkRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Headers.HeaderExists("SdkVersion");
    }

    public static bool IsGraphBetaRequest(Request request) =>
        IsGraphRequest(request) &&
        IsGraphBetaUrl(request.RequestUri);

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
    public static IList<MockResponseHeader> BuildGraphResponseHeaders(Request request, string requestId, string requestDate)
    {
        if (!IsGraphRequest(request))
        {
            return [];
        }

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
        if (request.Headers.FirstOrDefault((h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is not null)
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

        return path.Replace("~appFolder", AppFolder, StringComparison.OrdinalIgnoreCase);
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
            for (var i = 0; i < urlSegments.Length; i++)
            {
                var segment = urlSegments[i];
                var sanitizedSegment = SanitizePathSegment(i < 1 ? "" : urlSegments[i - 1], segment);
                resourceUrl = resourceUrl.Replace(segment, sanitizedSegment, StringComparison.OrdinalIgnoreCase);
            }
        }
        return $"{uri.GetLeftPart(UriPartial.Authority)}/{parsedSample.QueryVersion}/{resourceUrl}{queryString}";
    }

    public static async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidateJsonAsync(string? json, string? schemaUrl, HttpClient httpClient, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Validating JSON against schema {SchemaUrl}", schemaUrl);

            if (string.IsNullOrEmpty(json))
            {
                logger.LogDebug("JSON is empty, skipping validation");
                return (true, []);
            }
            if (string.IsNullOrEmpty(schemaUrl))
            {
                logger.LogDebug("Schema URL is empty, skipping validation");
                return (true, []);
            }
            ArgumentNullException.ThrowIfNull(httpClient);

            logger.LogDebug("Downloading schema from {SchemaUrl}", schemaUrl);
            var schemaContents = await httpClient.GetStringAsync(schemaUrl, cancellationToken);

            logger.LogDebug("Parsing schema");
            var schema = JSchema.Parse(schemaContents);
            logger.LogDebug("Parsing JSON");
            var token = JToken.Parse(json);

            logger.LogDebug("Validating JSON");
            var isValid = token.IsValid(schema, out IList<string> errorMessages);

            return (isValid, errorMessages);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error validating JSON");
            return (false, [ex.Message]);
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
        if (IsFunctionCall(segment))
        {
            var openingBracketIndex = segment.IndexOf('(', StringComparison.OrdinalIgnoreCase);
            var textWithinBrackets = segment.Substring(
                openingBracketIndex + 1,
                segment.Length - 2
            );
            var sanitizedText = string.Join(',', textWithinBrackets
                .Split(',')
                .Select(text =>
                {
                    if (text.Contains('=', StringComparison.OrdinalIgnoreCase))
                    {
                        var key = text.Split('=')[0];
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
