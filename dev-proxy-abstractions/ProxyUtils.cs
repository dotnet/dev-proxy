// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Abstractions;

class ParsedSample
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
    private static readonly JsonSerializerOptions jsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    // doesn't end with a path separator
    public static string? AppFolder => Path.GetDirectoryName(AppContext.BaseDirectory);

    public static readonly string ReportsKey = "Reports";

    static ProxyUtils()
    {
        // convert enum values to camelCase
        jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static bool IsGraphRequest(Request request) => IsGraphUrl(request.RequestUri);

    public static bool IsGraphUrl(Uri uri) =>
        uri.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.StartsWith("microsoftgraph.", StringComparison.OrdinalIgnoreCase);

    public static bool IsGraphBatchUrl(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/$batch", StringComparison.OrdinalIgnoreCase);

    public static Uri GetAbsoluteRequestUrlFromBatch(Uri batchRequestUri, string relativeRequestUrl)
    {
        var hostName = batchRequestUri.Host;
        var graphVersion = batchRequestUri.Segments[1].TrimEnd('/');
        var absoluteRequestUrl = new Uri($"https://{hostName}/{graphVersion}{relativeRequestUrl}");
        return absoluteRequestUrl;
    }

    public static bool IsSdkRequest(Request request) => request.Headers.HeaderExists("SdkVersion");

    public static bool IsGraphBetaRequest(Request request) =>
        IsGraphRequest(request) &&
        IsGraphBetaUrl(request.RequestUri);

    public static bool IsGraphBetaUrl(Uri uri) =>
        uri.AbsolutePath.Contains("/beta/", StringComparison.OrdinalIgnoreCase);

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

    // from: https://github.com/microsoftgraph/microsoft-graph-explorer-v4/blob/db86b903f36ef1b882996d46aee52cd49ed4444b/src/app/utils/query-url-sanitization.ts
    public static string SanitizeUrl(string absoluteUrl)
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
                return $"{match.Value[..match.Value.IndexOf(':')]}:<value>";
            });
            // Split requestUrl into segments that can be sanitized individually
            var urlSegments = resourceUrl.Split('/');
            for (var i = 0; i < urlSegments.Length; i++)
            {
                var segment = urlSegments[i];
                var sanitizedSegment = SanitizePathSegment(i < 1 ? "" : urlSegments[i - 1], segment);
                resourceUrl = resourceUrl.Replace(segment, sanitizedSegment);
            }
        }
        return $"{uri.GetLeftPart(UriPartial.Authority)}/{parsedSample.QueryVersion}/{resourceUrl}{queryString}";
    }

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
            var openingBracketIndex = segment.IndexOf('(');
            var textWithinBrackets = segment.Substring(
                openingBracketIndex + 1,
                segment.Length - 2
            );
            var sanitizedText = string.Join(',', textWithinBrackets
                .Split(',')
                .Select(text =>
                {
                    if (text.Contains('='))
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

        if (url != "")
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

    public static string GetGraphVersion(string url)
    {
        var uri = new Uri(url);
        return uri.Segments[1].Replace("/", "");
    }

    private static string GetRequestUrl(string url, string version)
    {
        var uri = new Uri(url);
        var versionToReplace = uri.AbsolutePath.StartsWith($"/{version}")
            ? version
            : GetGraphVersion(url);
        var requestContent = uri.AbsolutePath.Split(versionToReplace).LastOrDefault() ?? "";
        return Uri.UnescapeDataString(requestContent.TrimEnd('/')).TrimStart('/');
    }

    private static string GenerateSearchParameters(string url, string search)
    {
        var uri = new Uri(url);

        if (uri.Query != "")
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

    private static Assembly? _assembly;
    internal static Assembly GetAssembly()
            => _assembly ??= (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

    private static string _productVersion = string.Empty;
    public static string ProductVersion
    {
        get
        {
            if (_productVersion == string.Empty)
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

    public static void MergeHeaders(IList<MockResponseHeader> allHeaders, IList<MockResponseHeader> headersToAdd)
    {
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
                    allHeaders.Remove(existingHeader);
                    allHeaders.Add(new(header.Name, string.Join(", ", allValues)));
                    continue;
                }

                // don't remove headers that we've just added
                if (!headersToAdd.Contains(existingHeader))
                {
                    allHeaders.Remove(existingHeader);
                }
            }

            allHeaders.Add(header);
        }
    }

    public static JsonSerializerOptions JsonSerializerOptions => jsonSerializerOptions;
}
