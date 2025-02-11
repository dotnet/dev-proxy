// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Web;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

internal class HttpFile
{
    public Dictionary<string, string> Variables { get; set; } = [];
    public List<HttpFileRequest> Requests { get; set; } = [];

    public string Serialize()
    {
        var sb = new StringBuilder();

        foreach (var variable in Variables)
        {
            sb.AppendLine($"@{variable.Key} = {variable.Value}");
        }

        foreach (var request in Requests)
        {
            sb.AppendLine();
            sb.AppendLine("###");
            sb.AppendLine();
            sb.AppendLine($"# @name {GetRequestName(request)}");
            sb.AppendLine();

            sb.AppendLine($"{request.Method} {request.Url}");

            foreach (var header in request.Headers)
            {
                sb.AppendLine($"{header.Name}: {header.Value}");
            }

            if (!string.IsNullOrEmpty(request.Body))
            {
                sb.AppendLine();
                sb.AppendLine(request.Body);
            }
        }

        return sb.ToString();
    }

    private static string GetRequestName(HttpFileRequest request)
    {
        var url = new Uri(request.Url);
        return $"{request.Method.ToLower()}{url.Segments.Last().Replace("/", "").ToPascalCase()}";
    }
}

internal class HttpFileRequest
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Body { get; set; }
    public List<HttpFileRequestHeader> Headers { get; set; } = [];
}

internal class HttpFileRequestHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class HttpFileGeneratorPluginReport : List<string>
{
    public HttpFileGeneratorPluginReport() : base() { }

    public HttpFileGeneratorPluginReport(IEnumerable<string> collection) : base(collection) { }
}

internal class HttpFileGeneratorPluginConfiguration
{
    public bool IncludeOptionsRequests { get; set; } = false;
}

public class HttpFileGeneratorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(HttpFileGeneratorPlugin);
    public static readonly string GeneratedHttpFilesKey = "GeneratedHttpFiles";
    private HttpFileGeneratorPluginConfiguration _configuration = new();
    private readonly string[] headersToExtract = ["authorization", "key"];
    private readonly string[] queryParametersToExtract = ["key"];

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private async Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        Logger.LogInformation("Creating HTTP file from recorded requests...");

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return;
        }

        var httpFile = await GetHttpRequestsAsync(e.RequestLogs);
        DeduplicateRequests(httpFile);
        ExtractVariables(httpFile);

        var fileName = $"requests_{DateTime.Now:yyyyMMddHHmmss}.http";
        Logger.LogDebug("Writing HTTP file to {fileName}...", fileName);
        File.WriteAllText(fileName, httpFile.Serialize());
        Logger.LogInformation("Created HTTP file {fileName}", fileName);

        var generatedHttpFiles = new[] { fileName };
        StoreReport(new HttpFileGeneratorPluginReport(generatedHttpFiles), e);

        // store the generated HTTP files in the global data
        // for use by other plugins
        e.GlobalData[GeneratedHttpFilesKey] = generatedHttpFiles;
    }

    private async Task<HttpFile> GetHttpRequestsAsync(IEnumerable<RequestLog> requestLogs)
    {
        var httpFile = new HttpFile();

        foreach (var request in requestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null)
            {
                continue;
            }

            if (!_configuration.IncludeOptionsRequests &&
                string.Equals(request.Context.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Skipping OPTIONS request {url}...", request.Context.Session.HttpClient.Request.RequestUri);
                continue;
            }

            var methodAndUrlString = request.Message;
            Logger.LogDebug("Adding request {methodAndUrl}...", methodAndUrlString);

            var methodAndUrl = methodAndUrlString.Split(' ');
            httpFile.Requests.Add(new HttpFileRequest
            {
                Method = methodAndUrl[0],
                Url = methodAndUrl[1],
                Body = request.Context.Session.HttpClient.Request.HasBody ? await request.Context.Session.GetRequestBodyAsString() : null,
                Headers = request.Context.Session.HttpClient.Request.Headers
                    .Select(h => new HttpFileRequestHeader { Name = h.Name, Value = h.Value })
                    .ToList()
            });
        }

        return httpFile;
    }

    private void DeduplicateRequests(HttpFile httpFile)
    {
        Logger.LogDebug("Deduplicating requests...");

        // remove duplicate requests
        // if the request doesn't have a body, dedupe on method + URL
        // if it has a body, dedupe on method + URL + body
        var uniqueRequests = new List<HttpFileRequest>();
        foreach (var request in httpFile.Requests)
        {
            Logger.LogDebug("  Checking request {method} {url}...", request.Method, request.Url);

            var existingRequest = uniqueRequests.FirstOrDefault(r =>
            {
                if (r.Method != request.Method || r.Url != request.Url)
                {
                    return false;
                }

                if (r.Body is null && request.Body is null)
                {
                    return true;
                }

                if (r.Body is not null && request.Body is not null)
                {
                    return r.Body == request.Body;
                }

                return false;
            });

            if (existingRequest is null)
            {
                Logger.LogDebug("  Keeping request {method} {url}...", request.Method, request.Url);
                uniqueRequests.Add(request);
            }
            else
            {
                Logger.LogDebug("  Skipping duplicate request {method} {url}...", request.Method, request.Url);
            }
        }

        httpFile.Requests = uniqueRequests;
    }

    private void ExtractVariables(HttpFile httpFile)
    {
        Logger.LogDebug("Extracting variables...");

        foreach (var request in httpFile.Requests)
        {
            Logger.LogDebug("  Processing request {method} {url}...", request.Method, request.Url);

            foreach (var headerName in headersToExtract)
            {
                Logger.LogDebug("    Extracting header {headerName}...", headerName);

                var headers = request.Headers.Where(h => h.Name.Contains(headerName, StringComparison.OrdinalIgnoreCase));
                if (headers is not null)
                {
                    Logger.LogDebug("    Found {numHeaders} matching headers...", headers.Count());

                    foreach (var header in headers)
                    {
                        var variableName = GetVariableName(request, headerName);
                        Logger.LogDebug("    Extracting variable {variableName}...", variableName);
                        httpFile.Variables[variableName] = header.Value;
                        header.Value = $"{{{{{variableName}}}}}";
                    }
                }
            }

            var url = new Uri(request.Url);
            var query = HttpUtility.ParseQueryString(url.Query);
            if (query.Count > 0)
            {
                Logger.LogDebug("    Processing query parameters...");

                foreach (var queryParameterName in queryParametersToExtract)
                {
                    Logger.LogDebug("    Extracting query parameter {queryParameterName}...", queryParameterName);

                    var queryParams = query.AllKeys.Where(k => k is not null && k.Contains(queryParameterName, StringComparison.OrdinalIgnoreCase));
                    if (queryParams is not null)
                    {
                        Logger.LogDebug("    Found {numQueryParams} matching query parameters...", queryParams.Count());

                        foreach (var queryParam in queryParams)
                        {
                            var variableName = GetVariableName(request, queryParam!);
                            Logger.LogDebug("    Extracting variable {variableName}...", variableName);
                            httpFile.Variables[variableName] = queryParam!;
                            query[queryParam] = $"{{{{{variableName}}}}}";
                        }
                    }
                }
                request.Url = $"{url.GetLeftPart(UriPartial.Path)}?{query}"
                    .Replace("%7b", "{")
                    .Replace("%7d", "}");
                Logger.LogDebug("    Updated URL to {url}...", request.Url);
            }
            else
            {
                Logger.LogDebug("    No query parameters to process...");
            }
        }
    }

    private static string GetVariableName(HttpFileRequest request, string variableName)
    {
        var url = new Uri(request.Url);
        return $"{url.Host.Replace(".", "_").Replace("-", "_")}_{variableName.Replace("-", "_")}";
    }
}