// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Reporting;

public sealed class OpenApiValidationPluginConfiguration
{
    public bool ReportUnmatchedRequests { get; set; }
    public IEnumerable<string>? SpecFiles { get; set; }
    public string? SpecsFolder { get; set; }
}

public sealed class OpenApiValidationPlugin(
    HttpClient httpClient,
    ILogger<OpenApiValidationPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<OpenApiValidationPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    // headers that OpenAPI says to ignore when defined as header parameters
    private static readonly string[] _ignoredHeaderParameters = ["Accept", "Content-Type", "Authorization"];

    private const int MaxBodySize = 5 * 1024 * 1024;

    private readonly ConditionalWeakTable<IProxySession, ValidationOutcome> _outcomes = [];
    private List<LoadedSpec> _specs = [];

    public override string Name => nameof(OpenApiValidationPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        var specFiles = new List<string>();

        foreach (var specFile in Configuration.SpecFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(specFile))
            {
                continue;
            }

            var fullPath = ProxyUtils.GetFullPath(specFile, ProxyConfiguration.ConfigFile);
            if (!File.Exists(fullPath))
            {
                Enabled = false;
                throw new InvalidOperationException($"Spec file '{fullPath}' does not exist.");
            }
            specFiles.Add(fullPath);
        }

        if (!string.IsNullOrWhiteSpace(Configuration.SpecsFolder))
        {
            var folderPath = ProxyUtils.GetFullPath(Configuration.SpecsFolder, ProxyConfiguration.ConfigFile);
            if (!Directory.Exists(folderPath))
            {
                Enabled = false;
                throw new InvalidOperationException($"Specs folder '{folderPath}' does not exist.");
            }

            specFiles.AddRange(Directory
                .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSpecFile)
                .Order(StringComparer.Ordinal));
        }

        if (Configuration.SpecFiles?.Any() != true && string.IsNullOrWhiteSpace(Configuration.SpecsFolder))
        {
            Enabled = false;
            throw new InvalidOperationException("Specify at least one OpenAPI spec using specFiles or specsFolder.");
        }

        _specs = await LoadSpecsAsync(specFiles.Distinct(StringComparer.Ordinal), cancellationToken);
        if (_specs.Count == 0)
        {
            Logger.LogWarning("No valid OpenAPI specs with absolute server URLs found. Requests won't be validated.");
        }
    }

    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        var loggingContext = new LoggingContext(e.ProxySession);
        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, loggingContext);
            return Task.CompletedTask;
        }
        if (IsCorsPreflight(e.ProxySession.Request))
        {
            Logger.LogRequest("Skipping CORS preflight request", MessageType.Skipped, loggingContext);
            return Task.CompletedTask;
        }

        var outcome = GetOrValidate(e.ProxySession);
        foreach (var note in outcome.Notes)
        {
            Logger.LogRequest(note, MessageType.Normal, loggingContext);
        }
        if (outcome.Result is null)
        {
            Logger.LogRequest("No matching OpenAPI spec", MessageType.Skipped, loggingContext);
        }
        else if (outcome.Result.Issues.Any())
        {
            foreach (var issue in outcome.Result.Issues)
            {
                Logger.LogRequest(
                    $"OpenAPI {issue.Location.ToString().ToLowerInvariant()} validation: {issue}",
                    MessageType.Warning,
                    loggingContext);
            }
        }
        else
        {
            Logger.LogRequest($"Request and response match {outcome.Result.Operation}", MessageType.Processed, loggingContext);
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    public override Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        var sessions = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedResponse &&
                l.Context?.Session is not null &&
                l.Context.Session.HasResponse &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context.Session.Request.RequestUri.AbsoluteUri) &&
                !IsCorsPreflight(l.Context.Session.Request))
            .Select(l => l.Context!.Session)
            .Distinct();

        if (!sessions.Any())
        {
            Logger.LogRequest("No requests to process", MessageType.Skipped);
            return Task.CompletedTask;
        }

        var validated = 0;
        var results = new List<OpenApiValidationResult>();
        var unmatched = new List<string>();
        var skipped = new List<string>();

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = GetOrValidate(session);
            if (outcome.Result is null)
            {
                unmatched.Add($"{session.Request.Method} {session.Request.Url}");
                continue;
            }

            validated++;
            skipped.AddRange(outcome.Notes.Select(n => $"{session.Request.Method} {session.Request.Url}: {n}"));
            if (outcome.Result.Issues.Any())
            {
                results.Add(outcome.Result);
            }
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Validated {Validated} request(s) against OpenAPI specs. {WithIssues} request(s) with issues.",
                validated,
                results.Count);
        }

        var report = new OpenApiValidationPluginReport
        {
            ValidatedRequests = validated,
            Results = results,
            UnmatchedRequests = Configuration.ReportUnmatchedRequests ? [.. unmatched.Distinct()] : null,
            SkippedValidations = skipped.Count > 0 ? skipped : null
        };
        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
        return Task.CompletedTask;
    }

    internal OpenApiValidationResult? Validate(IProxySession session) => GetOrValidate(session).Result;

    internal IReadOnlyList<string> GetNotes(IProxySession session) => GetOrValidate(session).Notes;

    private ValidationOutcome GetOrValidate(IProxySession session) =>
        _outcomes.GetValue(session, s =>
        {
            var notes = new List<string>();
            return new ValidationOutcome(ValidateSession(s, notes), notes);
        });

    private OpenApiValidationResult? ValidateSession(IProxySession session, List<string> notes)
    {
        var request = session.Request;
        var response = session.Response;
        var requestText = $"{request.Method} {request.Url}";
        int? statusCode = response is null ? null : (int)response.StatusCode;

        var match = FindOperation(request);
        if (match is null)
        {
            return null;
        }

        var issues = new List<OpenApiValidationIssue>();
        var specFile = match.Spec.FilePath;

        if (match.PathTemplate is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = $"Path '{match.RelativePath}' isn't documented in the spec"
            });
            return new()
            {
                Request = requestText,
                StatusCode = statusCode,
                SpecFile = specFile,
                Issues = issues
            };
        }

        if (match.Operation is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = $"Method {request.Method} isn't documented for path '{match.PathTemplate}'"
            });
            return new()
            {
                Request = requestText,
                StatusCode = statusCode,
                SpecFile = specFile,
                Issues = issues
            };
        }

        var strict = match.Spec.StrictNullableEnums;
        ValidateParameters(request, match, issues);
        ValidateRequestBody(request, match.Operation, issues, notes, strict);
        if (response is not null)
        {
            ValidateResponse(response, match.Operation, issues, notes, strict);
        }

        return new()
        {
            Request = requestText,
            StatusCode = statusCode,
            Operation = $"{request.Method} {match.PathTemplate}",
            SpecFile = specFile,
            Issues = issues
        };
    }

    private OperationMatch? FindOperation(IHttpRequest request)
    {
        var requestUrl = request.RequestUri.GetLeftPart(UriPartial.Path);
        var method = new HttpMethod(request.Method);
        OperationMatch? firstPathMatch = null;
        OperationMatch? firstServerMatch = null;

        // prefer a spec that documents the requested method; fall back to
        // a matching path without the method, and then to a matching server
        foreach (var spec in _specs)
        {
            foreach (var server in spec.Servers)
            {
                var serverMatch = server.Regex.Match(requestUrl);
                if (!serverMatch.Success)
                {
                    continue;
                }

                var relativePath = requestUrl[serverMatch.Length..];
                if (relativePath.Length > 0 && relativePath[0] != '/')
                {
                    // server URL matched only part of a path segment
                    continue;
                }
                if (relativePath.Length == 0)
                {
                    relativePath = "/";
                }

                // paths are ordered from the most specific, which takes precedence
                // per the OpenAPI spec, even if it doesn't document the method
                var bestPath = FindPaths(server, relativePath).FirstOrDefault();
                if (bestPath.Path is { } path)
                {
                    if (path.Operations.TryGetValue(method, out var operation))
                    {
                        return new OperationMatch(spec, relativePath, path.Template, path.PathItem, operation, bestPath.PathValues);
                    }
                    firstPathMatch ??= new OperationMatch(spec, relativePath, path.Template, path.PathItem, null, bestPath.PathValues);
                }
                else
                {
                    firstServerMatch ??= new OperationMatch(spec, relativePath, null, null, null, []);
                }
            }
        }

        return firstPathMatch ?? firstServerMatch;
    }

    private static IEnumerable<(CompiledPath Path, Dictionary<string, string> PathValues)> FindPaths(ServerScope server, string relativePath)
    {
        // paths are ordered so that literal paths are preferred over templated paths
        // (e.g. /users/me over /users/{id}), and among templated paths, those with fewer parameters
        foreach (var path in server.Paths)
        {
            var m = path.Regex.Match(relativePath);
            if (!m.Success)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < path.ParameterNames.Count; i++)
            {
                values[path.ParameterNames[i]] = Uri.UnescapeDataString(m.Groups[i + 1].Value);
            }
            yield return (path, values);
        }
    }

    private static void ValidateParameters(IHttpRequest request, OperationMatch match, List<OpenApiValidationIssue> issues)
    {
        var parameters = GetEffectiveParameters(match.PathItem!, match.Operation!);
        var query = ParseQueryString(request.RequestUri.Query);

        foreach (var parameter in parameters)
        {
            if (parameter.Name is null || parameter.In is null)
            {
                continue;
            }

            List<string>? values = null;
            switch (parameter.In)
            {
                case ParameterLocation.Path:
                    if (match.PathValues.TryGetValue(parameter.Name, out var pathValue))
                    {
                        values = [pathValue];
                    }
                    break;
                case ParameterLocation.Query:
                    if (query.TryGetValue(parameter.Name, out var queryValues))
                    {
                        values = queryValues;
                    }
                    else if (GetObjectQueryKeys(parameter).Any(k => query.Keys.Any(k.IsMatch)))
                    {
                        // values of deepObject (e.g. filter[name]=value) and exploded
                        // form object (e.g. name=value) parameters aren't validated
                        continue;
                    }
                    break;
                case ParameterLocation.Header:
                    if (_ignoredHeaderParameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var headerValues = request.Headers.GetAll(parameter.Name).Select(h => h.Value).ToList();
                    if (headerValues.Count > 0)
                    {
                        values = headerValues;
                    }
                    break;
                default:
                    // cookie and querystring parameters aren't validated
                    continue;
            }

            var locationName = parameter.In.Value.ToString().ToLowerInvariant();
            if (values is null)
            {
                if (parameter.Required)
                {
                    issues.Add(new()
                    {
                        Location = OpenApiValidationIssueLocation.Request,
                        Path = parameter.Name,
                        Message = $"Missing required {locationName} parameter '{parameter.Name}'"
                    });
                }
                continue;
            }

            ValidateParameterValue(parameter.Name, $"{locationName} parameter", values, parameter.Schema,
                parameter.Style ?? GetDefaultStyle(parameter.In.Value), parameter.Explode,
                OpenApiValidationIssueLocation.Request, issues, match.Spec.StrictNullableEnums);
        }

        // unknown query parameters
        var hasQueryStringParameter = parameters.Any(p => p.In == ParameterLocation.QueryString);
        if (hasQueryStringParameter)
        {
            return;
        }

        var queryParameters = parameters
            .Where(p => p.In == ParameterLocation.Query && p.Name is not null)
            .ToArray();
        var knownQueryKeys = queryParameters
            .Select(p => new ObjectQueryKey(p.Name!, false))
            .Concat(queryParameters.SelectMany(GetObjectQueryKeys))
            .ToArray();
        foreach (var name in query.Keys)
        {
            if (knownQueryKeys.Any(k => k.IsMatch(name)))
            {
                continue;
            }

            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Path = name,
                Message = $"Query parameter '{name}' isn't documented"
            });
        }
    }

    private static ParameterStyle GetDefaultStyle(ParameterLocation location) => location switch
    {
        ParameterLocation.Query or ParameterLocation.Cookie => ParameterStyle.Form,
        _ => ParameterStyle.Simple
    };

    private readonly record struct ObjectQueryKey(string Name, bool IsPrefix)
    {
        public bool IsMatch(string key) => IsPrefix
            ? key.StartsWith(Name, StringComparison.Ordinal)
            : string.Equals(key, Name, StringComparison.Ordinal);
    }

    // Query keys used to serialize object parameters: deepObject parameters
    // are sent as filter[name]=value and exploded form objects as name=value
    private static IEnumerable<ObjectQueryKey> GetObjectQueryKeys(IOpenApiParameter parameter)
    {
        if (parameter.Name is null)
        {
            return [];
        }

        if (parameter.Style == ParameterStyle.DeepObject)
        {
            return [new($"{parameter.Name}[", true)];
        }

        var type = OpenApiSchemaValidator.GetEffectiveType(parameter.Schema);
        if ((parameter.Style ?? ParameterStyle.Form) == ParameterStyle.Form && parameter.Explode &&
            type is not null && type.Value.HasFlag(JsonSchemaType.Object))
        {
            return OpenApiSchemaValidator.GetPropertyNames(parameter.Schema).Select(p => new ObjectQueryKey(p, false));
        }

        return [];
    }

    private static void ValidateParameterValue(string name, string kind, List<string> values, IOpenApiSchema? schema,
        ParameterStyle style, bool explode, OpenApiValidationIssueLocation location, List<OpenApiValidationIssue> issues,
        bool strictNullableEnums)
    {
        if (schema is null || OpenApiSchemaValidator.GetEffectiveType(schema) == JsonSchemaType.Object)
        {
            return;
        }

        var value = OpenApiSchemaValidator.CoerceParameterValue(values, schema, name, style, explode);
        var direction = location == OpenApiValidationIssueLocation.Request
            ? SchemaValidationDirection.Request
            : SchemaValidationDirection.Response;
        var errors = OpenApiSchemaValidator.Validate(value, schema, direction, name, strictNullableEnums);
        if (errors.Count > 0)
        {
            // the value might be valid as a string when the schema allows
            // multiple types, e.g. anyOf: [{ type: integer, enum: [1] }, { type: string }]
            var raw = OpenApiSchemaValidator.CoerceParameterValue(values, schema, name, style, explode, coerce: false);
            if (OpenApiSchemaValidator.Validate(raw, schema, direction, name, strictNullableEnums).Count == 0)
            {
                return;
            }
        }

        foreach (var error in errors)
        {
            issues.Add(new()
            {
                Location = location,
                Path = error.Path,
                Message = $"Invalid {kind} value '{string.Join(",", values)}': {error.Message}"
            });
        }
    }

    private static void ValidateRequestBody(IHttpRequest request, OpenApiOperation operation, List<OpenApiValidationIssue> issues, List<string> notes, bool strictNullableEnums)
    {
        var requestBody = operation.RequestBody;
        var hasBody = request.Body.Length > 0;

        if (!hasBody)
        {
            if (requestBody?.Required == true && !request.HasBody)
            {
                issues.Add(new()
                {
                    Location = OpenApiValidationIssueLocation.Request,
                    Message = "Missing required request body"
                });
            }
            return;
        }

        if (requestBody is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = "Request body isn't documented for this operation"
            });
            return;
        }

        ValidateBody(request, requestBody.Content, OpenApiValidationIssueLocation.Request, issues, notes, strictNullableEnums);
    }

    private static void ValidateResponse(IHttpResponse response, OpenApiOperation operation, List<OpenApiValidationIssue> issues, List<string> notes, bool strictNullableEnums)
    {
        var statusCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var responses = operation.Responses;
        IOpenApiResponse? specResponse = null;
        if (responses is not null)
        {
            specResponse =
                responses.FirstOrDefault(r => r.Key == statusCode).Value ??
                responses.FirstOrDefault(r => string.Equals(r.Key, $"{statusCode[0]}XX", StringComparison.OrdinalIgnoreCase)).Value ??
                responses.FirstOrDefault(r => string.Equals(r.Key, "default", StringComparison.OrdinalIgnoreCase)).Value;
        }

        if (specResponse is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Response,
                Message = $"Status code {statusCode} isn't documented for this operation"
            });
            return;
        }

        foreach (var (headerName, header) in specResponse.Headers ?? new Dictionary<string, IOpenApiHeader>())
        {
            if (string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = response.Headers.GetAll(headerName).Select(h => h.Value).ToList();
            if (values.Count == 0)
            {
                if (header.Required)
                {
                    issues.Add(new()
                    {
                        Location = OpenApiValidationIssueLocation.Response,
                        Path = headerName,
                        Message = $"Missing required response header '{headerName}'"
                    });
                }
                continue;
            }

            ValidateParameterValue(headerName, "response header", values, header.Schema,
                header.Style ?? ParameterStyle.Simple, header.Explode,
                OpenApiValidationIssueLocation.Response, issues, strictNullableEnums);
        }

        if (response.Body.Length == 0)
        {
            return;
        }

        if (specResponse.Content is not { Count: > 0 })
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Response,
                Message = $"Response body isn't documented for status code {statusCode}"
            });
            return;
        }

        ValidateBody(response, specResponse.Content, OpenApiValidationIssueLocation.Response, issues, notes, strictNullableEnums);
    }

    private static void ValidateBody(IHttpMessage message, IDictionary<string, IOpenApiMediaType>? content,
        OpenApiValidationIssueLocation location, List<OpenApiValidationIssue> issues, List<string> notes, bool strictNullableEnums)
    {
        if (content is not { Count: > 0 })
        {
            return;
        }

        var locationName = location.ToString().ToLowerInvariant();
        var contentType = GetMediaType(message.ContentType);
        if (contentType is null)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"The {locationName} has a body but no Content-Type header"
            });
            return;
        }

        var mediaType = FindMediaType(content, contentType);
        if (mediaType is null)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"Content type '{contentType}' isn't documented. Documented: {string.Join(", ", content.Keys)}"
            });
            return;
        }

        if (!IsJsonMediaType(contentType))
        {
            return;
        }

        // validation runs inline while processing the response, so skip
        // large bodies to avoid stalling the proxy
        if (message.Body.Length > MaxBodySize)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Skipped validating the {locationName} body because it's {message.Body.Length:N0} bytes, which exceeds the maximum of {MaxBodySize:N0} bytes"));
            return;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(message.Body);
        }
        catch (JsonException ex)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"The {locationName} body isn't valid JSON: {ex.Message}"
            });
            return;
        }

        using (json)
        {
            var direction = location == OpenApiValidationIssueLocation.Request
                ? SchemaValidationDirection.Request
                : SchemaValidationDirection.Response;
            foreach (var error in OpenApiSchemaValidator.Validate(json.RootElement, mediaType.Schema, direction, strictNullableEnums: strictNullableEnums))
            {
                issues.Add(new()
                {
                    Location = location,
                    Path = error.Path,
                    Message = error.Message
                });
            }
        }
    }

    private static IOpenApiMediaType? FindMediaType(IDictionary<string, IOpenApiMediaType> content, string contentType)
    {
        var entries = content
            .Select(c => (MediaType: GetMediaType(c.Key) ?? c.Key, c.Value))
            .ToArray();

        var exact = entries.FirstOrDefault(c => string.Equals(c.MediaType, contentType, StringComparison.OrdinalIgnoreCase));
        if (exact.Value is not null)
        {
            return exact.Value;
        }

        var typeWildcard = $"{contentType.Split('/')[0]}/*";
        var wildcard = entries.FirstOrDefault(c => string.Equals(c.MediaType, typeWildcard, StringComparison.OrdinalIgnoreCase));
        if (wildcard.Value is not null)
        {
            return wildcard.Value;
        }

        return entries.FirstOrDefault(c => c.MediaType == "*/*").Value;
    }

    private static string? GetMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        return contentType.Split(';')[0].Trim().ToLowerInvariant();
    }

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType == "application/json" ||
        mediaType.EndsWith("+json", StringComparison.Ordinal);

    private static List<IOpenApiParameter> GetEffectiveParameters(IOpenApiPathItem pathItem, OpenApiOperation operation)
    {
        var parameters = new List<IOpenApiParameter>();
        parameters.AddRange(pathItem.Parameters ?? []);

        // operation-level parameters override path-level parameters with the same name and location
        foreach (var parameter in operation.Parameters ?? [])
        {
            _ = parameters.RemoveAll(p => p.Name == parameter.Name && p.In == parameter.In);
            parameters.Add(parameter);
        }

        return parameters;
    }

    private static Dictionary<string, List<string>> ParseQueryString(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Unescape(separatorIndex < 0 ? pair : pair[..separatorIndex]);
            var value = separatorIndex < 0 ? string.Empty : Unescape(pair[(separatorIndex + 1)..]);

            if (!result.TryGetValue(name, out var values))
            {
                values = [];
                result[name] = values;
            }
            values.Add(value);
        }

        return result;
    }

    private static string Unescape(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool IsCorsPreflight(IHttpRequest request) =>
        string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) &&
        request.Headers.Contains("Access-Control-Request-Method");

    private static bool IsSpecFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<LoadedSpec>> LoadSpecsAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var specs = new List<LoadedSpec>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Logger.LogDebug("Loading OpenAPI spec from '{File}'...", file);
            OpenApiDocument? document;
            string? diagnostics;
            var strictNullableEnums = false;
            try
            {
                var fileContents = await File.ReadAllTextAsync(file, cancellationToken);
                fileContents = ProxyUtils.ReplaceVariables(fileContents, ProxyConfiguration.Env, v => $"{{{v}}}");

                var settings = new OpenApiReaderSettings();
                settings.AddYamlReader();
                var readResult = OpenApiDocument.Parse(fileContents, null, settings);
                document = readResult.Document;
                strictNullableEnums = readResult.Diagnostic?.SpecificationVersion >= OpenApiSpecVersion.OpenApi3_1;
                diagnostics = readResult.Diagnostic?.Errors is { Count: > 0 } errors
                    ? string.Join("; ", errors.Select(e => e.ToString()))
                    : null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load OpenAPI spec from '{File}'", file);
                continue;
            }

            if (document is null)
            {
                Logger.LogWarning("File '{File}' isn't a valid OpenAPI spec. Skipping. {Diagnostics}", file, diagnostics);
                continue;
            }

            if (diagnostics is not null)
            {
                // the spec was loaded, but parts of it might've been ignored
                Logger.LogWarning("OpenAPI spec '{File}' has errors and might not be validated completely: {Diagnostics}", file, diagnostics);
            }

            var servers = BuildServerScopes(document, file);
            if (servers.Count == 0)
            {
                Logger.LogWarning("OpenAPI spec '{File}' doesn't define any absolute server URLs. Skipping", file);
                continue;
            }

            specs.Add(new LoadedSpec(file, servers, strictNullableEnums));
            Logger.LogInformation("Loaded OpenAPI spec '{File}'", file);
        }

        return specs;
    }

    private List<ServerScope> BuildServerScopes(OpenApiDocument document, string file)
    {
        // each operation is served from its effective servers: operation-level
        // servers override path-level servers, which override document-level servers
        var scopes = new Dictionary<string, (Regex Regex, Dictionary<string, CompiledPath> Paths)>(StringComparer.Ordinal);
        var skippedServers = new HashSet<string>(StringComparer.Ordinal);

        (Regex Regex, Dictionary<string, CompiledPath> Paths)? GetScope(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.Contains("://", StringComparison.Ordinal))
            {
                if (skippedServers.Add(url ?? string.Empty))
                {
                    Logger.LogDebug("Skipping server '{ServerUrl}' in '{File}' because it isn't an absolute URL", url, file);
                }
                return null;
            }

            var key = url.TrimEnd('/');
            if (!scopes.TryGetValue(key, out var scope))
            {
                scope = (CompileServerUrl(key), new(StringComparer.Ordinal));
                scopes[key] = scope;
            }
            return scope;
        }

        foreach (var (template, pathItem) in document.Paths ?? [])
        {
            var pathServers = pathItem.Servers is { Count: > 0 } ? pathItem.Servers : document.Servers ?? [];

            // register the path for its servers even if it has no operations
            // there, so that requests with undocumented methods are reported
            foreach (var server in pathServers)
            {
                if (GetScope(server.Url) is { } scope)
                {
                    GetOrAddPath(scope.Paths, template, pathItem);
                }
            }

            foreach (var (method, operation) in pathItem.Operations ?? new Dictionary<HttpMethod, OpenApiOperation>())
            {
                var operationServers = operation.Servers is { Count: > 0 } ? operation.Servers : pathServers;
                foreach (var server in operationServers)
                {
                    if (GetScope(server.Url) is { } scope)
                    {
                        GetOrAddPath(scope.Paths, template, pathItem).Operations[method] = operation;
                    }
                }
            }
        }

        // servers without paths still need to be matched so that requests
        // to undocumented paths are reported
        foreach (var server in document.Servers ?? [])
        {
            _ = GetScope(server.Url);
        }

        return [.. scopes.Values.Select(s => new ServerScope(
            s.Regex,
            [.. s.Paths.Values.OrderBy(p => p.ParameterNames.Count)]))];
    }

    private static CompiledPath GetOrAddPath(Dictionary<string, CompiledPath> paths, string template, IOpenApiPathItem pathItem)
    {
        if (!paths.TryGetValue(template, out var path))
        {
            path = CompilePath(template, pathItem);
            paths[template] = path;
        }
        return path;
    }

    private static Regex CompileServerUrl(string url)
    {
        // scheme and host are case-insensitive, but the base path is case-sensitive
        var authorityStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = url.IndexOf('/', authorityStart);
        var origin = pathStart < 0 ? url : url[..pathStart];
        // request URLs omit default ports, e.g. https://api.contoso.com:443 -> https://api.contoso.com
        origin = Regex.Replace(origin, "^(?:(?<o>https://[^/]*):443|(?<o>http://[^/]*):80)$", "${o}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var basePath = pathStart < 0 ? string.Empty : url[pathStart..];

        static string ToPattern(string value) =>
            Regex.Replace(Regex.Escape(value), @"\\\{[^}]+\}", "[^/]*");

        return new Regex($"^(?i:{ToPattern(origin)}){ToPattern(basePath)}", RegexOptions.CultureInvariant);
    }

    private static CompiledPath CompilePath(string template, IOpenApiPathItem pathItem)
    {
        var parameterNames = new List<string>();
        var pattern = Regex.Replace(Regex.Escape(template), @"\\\{([^}]+)\}", m =>
        {
            parameterNames.Add(Regex.Unescape(m.Groups[1].Value));
            return "([^/]+)";
        });
        var regex = new Regex($"^{pattern}$", RegexOptions.CultureInvariant);
        return new CompiledPath(template, pathItem, regex, parameterNames, []);
    }

    private sealed record CompiledPath(
        string Template,
        IOpenApiPathItem PathItem,
        Regex Regex,
        List<string> ParameterNames,
        Dictionary<HttpMethod, OpenApiOperation> Operations);

    private sealed record ServerScope(Regex Regex, List<CompiledPath> Paths);

    // OAS 3.1 requires null to be listed in enums, unlike OAS 3.0 nullable enums
    private sealed record LoadedSpec(string FilePath, List<ServerScope> Servers, bool StrictNullableEnums);

    private sealed record OperationMatch(
        LoadedSpec Spec,
        string RelativePath,
        string? PathTemplate,
        IOpenApiPathItem? PathItem,
        OpenApiOperation? Operation,
        Dictionary<string, string> PathValues);

    private sealed record ValidationOutcome(OpenApiValidationResult? Result, IReadOnlyList<string> Notes);
}
