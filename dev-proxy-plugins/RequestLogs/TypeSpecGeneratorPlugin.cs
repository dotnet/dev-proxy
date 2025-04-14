// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Plugins.TypeSpec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Web;

namespace DevProxy.Plugins.RequestLogs;

public class TypeSpecGeneratorPluginReportItem
{
    public required string ServerUrl { get; init; }
    public required string FileName { get; init; }
}

public class TypeSpecGeneratorPluginReport : List<TypeSpecGeneratorPluginReportItem>
{
    public TypeSpecGeneratorPluginReport() : base() { }

    public TypeSpecGeneratorPluginReport(IEnumerable<TypeSpecGeneratorPluginReportItem> collection) : base(collection) { }
}

internal class TypeSpecGeneratorPluginConfiguration
{
    public bool IgnoreResponseTypes { get; set; } = false;
}

public class TypeSpecGeneratorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(TypeSpecGeneratorPlugin);
    private readonly TypeSpecGeneratorPluginConfiguration _configuration = new();
    public static readonly string GeneratedTypeSpecFilesKey = "GeneratedTypeSpecFiles";

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        Logger.LogInformation("Creating TypeSpec files from recorded requests...");

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        var typeSpecFiles = new List<TypeSpecFile>();

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null ||
              request.Url is null ||
              request.Method is null ||
              // TypeSpec does not support OPTIONS requests
              string.Equals(request.Context.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) ||
              !ProxyUtils.MatchesUrlToWatch(UrlsToWatch, request.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                continue;
            }

            var httpRequest = request.Context.Session.HttpClient.Request;
            var httpResponse = request.Context.Session.HttpClient.Response;

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Processing request {methodAndUrlString}...", methodAndUrlString);

            var url = new Uri(request.Url);
            var doc = await GetOrCreateTypeSpecFile(typeSpecFiles, url);

            var serverUrl = url.GetLeftPart(UriPartial.Authority);
            if (!doc.Service.Servers.Any(x => x.Url.Equals(serverUrl, StringComparison.InvariantCultureIgnoreCase)))
            {
                doc.Service.Servers.Add(new Server
                {
                    Url = serverUrl
                });
            }

            var (route, parameters) = await GetRouteAndParameters(url);
            var op = new Operation
            {
                Name = await GetOperationName(request.Method, url),
                Description = await GetOperationDescription(request.Method, url),
                Method = Enum.Parse<HttpVerb>(request.Method, true),
                Route = route,
            };
            op.Parameters.AddRange(parameters);

            var lastSegment = GetLastNonParametrizableSegment(url);
            if (httpRequest.HasBody)
            {
                var models = await GetModelsFromString(httpRequest.BodyString, lastSegment.ToPascalCase());
                if (models.Length > 0)
                {
                    foreach (var model in models)
                    {
                        doc.Service.Namespace.MergeModel(model);
                    }

                    var rootModel = models.Last();
                    op.Parameters.Add(new()
                    {
                        Name = (rootModel.IsArray ? (await MakeSingular(rootModel.Name)) : rootModel.Name).ToCamelCase(),
                        Value = rootModel.Name,
                        In = ParameterLocation.Body
                    });
                }
            }

            foreach (var header in httpRequest.Headers)
            {
                if (Http.StandardHeaders.Contains(header.Name.ToLowerInvariant()) ||
                    Http.AuthHeaders.Contains(header.Name.ToLowerInvariant()))
                {
                    continue;
                }

                op.Parameters.Add(new()
                {
                    Name = Parameter.GetHeaderName(header.Name),
                    Value = header.Value,
                    In = ParameterLocation.Header
                });
            }

            if (httpResponse is not null)
            {
                OperationResponseModel res;

                if (_configuration.IgnoreResponseTypes)
                {
                    res = new OperationResponseModel
                    {
                        StatusCode = httpResponse.StatusCode,
                        BodyType = "string"
                    };
                }
                else
                {
                    res = new OperationResponseModel
                    {
                        StatusCode = httpResponse.StatusCode,
                        Headers = httpResponse.Headers
                            .Where(h => !Http.StandardHeaders.Contains(h.Name.ToLowerInvariant()) &&
                                        !Http.AuthHeaders.Contains(h.Name.ToLowerInvariant()))
                            .ToDictionary(h => h.Name.ToCamelCase(), h => h.Value.GetType().Name)
                    };

                    var models = await GetModelsFromString(httpResponse.BodyString, lastSegment.ToPascalCase(), httpResponse.StatusCode >= 400);
                    if (models.Length > 0)
                    {
                        foreach (var model in models)
                        {
                            doc.Service.Namespace.MergeModel(model);
                        }

                        var rootModel = models.Last();
                        if (rootModel.IsArray)
                        {
                            res.BodyType = $"{await MakeSingular(rootModel.Name)}[]";
                            op.Name = await GetOperationName("list", url);
                        }
                        else
                        {
                            res.BodyType = rootModel.Name;
                        }
                    }
                }

                op.MergeResponse(res);
            }

            doc.Service.Namespace.MergeOperation(op);
        }

        Logger.LogDebug("Serializing TypeSpec files...");
        var generatedTypeSpecFiles = new Dictionary<string, string>();
        foreach (var typeSpecFile in typeSpecFiles)
        {
            var fileName = $"{typeSpecFile.Name}-{DateTime.Now:yyyyMMddHHmmss}.tsp";
            Logger.LogDebug("Writing OpenAPI spec to {fileName}...", fileName);
            File.WriteAllText(fileName, typeSpecFile.ToString());

            generatedTypeSpecFiles.Add(typeSpecFile.Service.Servers.First().Url, fileName);

            Logger.LogInformation("Created OpenAPI spec file {fileName}", fileName);
        }

        StoreReport(new TypeSpecGeneratorPluginReport(
            generatedTypeSpecFiles
            .Select(kvp => new TypeSpecGeneratorPluginReportItem
            {
                ServerUrl = kvp.Key,
                FileName = kvp.Value
            })), e);

        // store the generated TypeSpec files in the global data
        // for use by other plugins
        e.GlobalData[GeneratedTypeSpecFilesKey] = generatedTypeSpecFiles;
    }

    private async Task<TypeSpecFile> GetOrCreateTypeSpecFile(List<TypeSpecFile> files, Uri url)
    {
        Logger.LogTrace("Entered GetOrCreateTypeSpecFile");

        var name = GetName(url);
        Logger.LogDebug("Name: {name}", name);
        var file = files.FirstOrDefault(d => d.Name == name);
        if (file is null)
        {
            Logger.LogDebug("Creating new TypeSpec file: {name}", name);

            var serviceTitle = await GetServiceTitle(url);
            file = new TypeSpecFile
            {
                Name = name,
                Service = new()
                {
                    Title = serviceTitle,
                    Namespace = new()
                    {
                        Name = GetRootNamespaceName(url)
                    }
                }
            };
            files.Add(file);
        }
        else
        {
            Logger.LogDebug("Using existing TypeSpec file: {name}", name);
        }

        Logger.LogTrace("Left GetOrCreateTypeSpecFile");

        return file;
    }

    private string GetRootNamespaceName(Uri url)
    {
        Logger.LogTrace("Entered GetRootNamespaceName");

        var ns = string.Join("", url.Host.Split('.').Select(x => x.ToPascalCase()));

        Logger.LogDebug("Root namespace name: {ns}", ns);
        Logger.LogTrace("Left GetRootNamespaceName");

        return ns;
    }

    private async Task<string> GetOperationName(string method, Uri url)
    {
        Logger.LogTrace("Entered GetOperationName");

        var lastSegment = GetLastNonParametrizableSegment(url);
        Logger.LogDebug("Url: {url}", url);
        Logger.LogDebug("Last non-parametrizable segment: {lastSegment}", lastSegment);

        var operationName = $"{method.ToLowerInvariant()}{(method == "list" ? lastSegment : await MakeSingular(lastSegment)).ToPascalCase()}";

        Logger.LogDebug("Operation name: {operationName}", operationName);
        Logger.LogTrace("Left GetOperationName");

        return operationName;
    }

    private async Task<string> GetOperationDescription(string method, Uri url)
    {
        Logger.LogTrace("Entered GetOperationDescription");

        var prompt = $"You're an expert in OpenAPI. You help developers build great OpenAPI specs for use with LLMs. For the specified request, generate a one-sentence description. Respond with just the description. For example, for a request such as `GET https://api.contoso.com/books/{{books-id}}` you return `Get a book by ID`. Request: {method.ToUpper()} {url}";
        ILanguageModelCompletionResponse? description = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            description = await Context.LanguageModelClient.GenerateCompletionAsync(prompt);
        }

        var operationDescription = description?.Response ?? $"{method.ToUpperInvariant()} {url}";

        Logger.LogDebug("Operation description: {operationDescription}", operationDescription);
        Logger.LogTrace("Left GetOperationDescription");

        return operationDescription;
    }

    private string GetName(Uri url)
    {
        Logger.LogTrace("Entered GetName");

        var name = url.Host.Replace(".", "-").ToKebabCase();

        Logger.LogDebug("Name: {name}", name);
        Logger.LogTrace("Left GetName");

        return name;
    }

    private async Task<string> GetServiceTitle(Uri url)
    {
        Logger.LogTrace("Entered GetServiceTitle");

        var prompt = $"Based on the following host name, generate a descriptive name of an API service hosted on this URL. Respond with just the name. Host name: {url.Host}";
        ILanguageModelCompletionResponse? serviceTitle = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            serviceTitle = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
        }
        var st = serviceTitle?.Response?.Trim('"') ?? $"{url.Host.Split('.').First().ToPascalCase()} API";

        Logger.LogDebug("Service title: {st}", st);
        Logger.LogTrace("Left GetServiceTitle");

        return st;
    }

    private string GetLastNonParametrizableSegment(Uri url)
    {
        Logger.LogTrace("Entered GetLastNonParametrizableSegment");

        var segments = url.Segments;
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i].Trim('/');
            if (!IsParametrizable(segment))
            {
                Logger.LogDebug("Last non-parametrizable segment: {segment}", segment);
                Logger.LogTrace("Left GetLastNonParametrizableSegment");

                return segment;
            }
        }

        Logger.LogDebug("No non-parametrizable segment found, returning empty string");
        Logger.LogTrace("Left GetLastNonParametrizableSegment");

        return string.Empty;
    }

    private bool IsParametrizable(string segment)
    {
        Logger.LogTrace("Entered IsParametrizable");

        var isParametrizable = Guid.TryParse(segment, out _) ||
          int.TryParse(segment, out _);

        Logger.LogDebug("Is segment '{segment}' parametrizable? {isParametrizable}", segment, isParametrizable);
        Logger.LogTrace("Left IsParametrizable");

        return isParametrizable;
    }

    private async Task<(string Route, Parameter[] Parameters)> GetRouteAndParameters(Uri url)
    {
        Logger.LogTrace("Entered GetRouteAndParameters");

        var route = new List<string>();
        var parameters = new List<Parameter>();
        var previousSegment = "item";

        foreach (var segment in url.Segments)
        {
            Logger.LogDebug("Processing segment: {segment}", segment);

            var segmentTrimmed = segment.Trim('/');
            if (string.IsNullOrEmpty(segmentTrimmed))
            {
                continue;
            }

            if (IsParametrizable(segmentTrimmed))
            {
                var paramName = $"{previousSegment}Id";
                parameters.Add(new Parameter
                {
                    Name = paramName,
                    Value = "string",
                    In = ParameterLocation.Path
                });
                route.Add($"{{{paramName}}}");
            }
            else
            {
                previousSegment = (await MakeSingular(segmentTrimmed)).ToCamelCase();
                route.Add(segmentTrimmed);
            }
        }

        if (url.Query.Length > 0)
        {
            Logger.LogDebug("Processing query string: {query}", url.Query);

            var query = HttpUtility.ParseQueryString(url.Query);
            foreach (string key in query.Keys)
            {
                parameters.Add(new()
                {
                    Name = key.ToCamelCase(),
                    Value = "string",
                    In = ParameterLocation.Query
                });
            }
        }
        else
        {
            Logger.LogDebug("No query string found in URL: {url}", url);
        }

        Logger.LogTrace("Left GetRouteAndParameters");

        return (string.Join('/', route), parameters.ToArray());
    }

    private async Task<Model[]> GetModelsFromString(string? str, string name, bool isError = false)
    {
        Logger.LogTrace("Entered GetModelsFromString");

        if (string.IsNullOrEmpty(str))
        {
            Logger.LogDebug("Empty string, returning empty model list");
            return [];
        }

        var models = new List<Model>();

        try
        {
            using var doc = JsonDocument.Parse(str);
            JsonElement root = doc.RootElement;
            await AddModelFromJsonElement(root, name, isError, models);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Failed to parse JSON string, returning empty model list. Exception: {ex}", ex.Message);

            // If the string is not a valid JSON, we return an empty model list
            return [];
        }

        Logger.LogTrace("Left GetModelsFromString");

        return [.. models];
    }

    private async Task<string> AddModelFromJsonElement(JsonElement jsonElement, string name, bool isError, List<Model> models)
    {
        Logger.LogTrace("Entered AddModelFromJsonElement");

        var model = new Model
        {
            Name = await MakeSingular(name),
            IsError = isError
        };

        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.String:
                return "string";
            case JsonValueKind.Number:
                return "int32";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return "boolean";
            case JsonValueKind.Object:
                if (jsonElement.GetPropertyCount() == 0)
                {
                    models.Add(new Model
                    {
                        Name = "Empty",
                        IsError = isError
                    });
                    return "Empty";
                }

                foreach (var p in jsonElement.EnumerateObject())
                {
                    var property = new ModelProperty
                    {
                        Name = p.Name,
                        Type = await AddModelFromJsonElement(p.Value, p.Name.ToPascalCase(), isError, models)
                    };
                    model.Properties.Add(property);
                }
                models.Add(model);
                return model.Name;
            case JsonValueKind.Array:
                await AddModelFromJsonElement(jsonElement.EnumerateArray().FirstOrDefault(), name, isError, models);
                model.IsArray = true;
                model.Name = name;
                models.Add(model);
                return $"{name}[]";
            default:
                return string.Empty;
        }
    }

    private async Task<string> MakeSingular(string noun)
    {
        Logger.LogTrace("Entered MakeSingular");

        var prompt = $"Make the following noun singular. Respond only with the word and nothing else. Don't translate. Word: {noun}";
        ILanguageModelCompletionResponse? singularNoun = null;
        if (await Context.LanguageModelClient.IsEnabledAsync())
        {
            singularNoun = await Context.LanguageModelClient.GenerateCompletionAsync(prompt, new() { Temperature = 1 });
        }
        var singular = singularNoun?.Response;

        if (singular is null ||
            string.IsNullOrEmpty(singular) ||
            singular.Contains(' '))
        {
            singular = noun.EndsWith('s') && !noun.EndsWith("ss") ? noun[0..^1] : noun;
            Logger.LogDebug("Failed to get singular form of {noun} from LLM. Using fallback: {singular}", noun, singular);
        }

        Logger.LogDebug("Singular form of '{noun}': {singular}", noun, singular);
        Logger.LogTrace("Left MakeSingular");

        return singular;
    }
}