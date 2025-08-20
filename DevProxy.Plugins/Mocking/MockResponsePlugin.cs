// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
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
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v1.0.0/mockresponseplugin.mocksfile.schema.json";
}

public class MockResponsePlugin(
    HttpClient httpClient,
    ILogger<MockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
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

    protected IProxyStorage ProxyStorage => proxyStorage;

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

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (Configuration.NoMocks)
        {
            Logger.LogRequest("Mocks disabled", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        var matchingResponse = await GetMatchingMockResponse(args.Request);
        if (matchingResponse is not null)
        {
            // we need to clone the response so that we're not modifying
            // the original that might be used in other requests
            var clonedResponse = (MockResponse)matchingResponse.Clone();
            var httpResponse = ProcessMockResponseInternal(args.Request, clonedResponse, args.RequestId);
            return PluginResponse.Respond(httpResponse);
        }
        else if (Configuration.BlockUnmockedRequests)
        {
            var errorResponse = ProcessMockResponseInternal(args.Request, new()
            {
                Request = new()
                {
                    Url = args.Request.RequestUri!.ToString(),
                    Method = args.Request.Method.Method
                },
                Response = new()
                {
                    StatusCode = 502,
                    Body = new GraphErrorResponseBody(new()
                    {
                        Code = "Bad Gateway",
                        Message = $"No mock response found for {args.Request.Method} {args.Request.RequestUri}"
                    })
                }
            }, args.RequestId);
            return PluginResponse.Respond(errorResponse);
        }

        Logger.LogRequest("No matching mock response found", MessageType.Skipped, args.Request);

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return PluginResponse.Continue();
    };

    protected virtual void ProcessMockResponse(ref byte[] body, IList<MockResponseHeader> headers, HttpRequestMessage request, MockResponse? matchingResponse)
    {
    }

    protected virtual void ProcessMockResponse(ref string? body, IList<MockResponseHeader> headers, HttpRequestMessage request, MockResponse? matchingResponse)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        ProcessMockResponse(ref bytes, headers, request, matchingResponse);
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

    private async Task<MockResponse?> GetMatchingMockResponse(HttpRequestMessage request)
    {
        if (Configuration.NoMocks ||
            Configuration.Mocks is null ||
            !Configuration.Mocks.Any())
        {
            return null;
        }

        var requestUrl = request.RequestUri!.ToString();
        var requestMethod = request.Method.Method;

        MockResponse? matchingMockResponse = null;

        foreach (var mockResponse in Configuration.Mocks)
        {
            if (mockResponse.Request is null)
            {
                continue;
            }

            if (mockResponse.Request.Method != requestMethod)
            {
                continue;
            }

            var urlMatches = false;
            if (mockResponse.Request.Url == requestUrl)
            {
                urlMatches = true;
            }
            else if (mockResponse.Request.Url.Contains('*', StringComparison.OrdinalIgnoreCase))
            {
                // turn mock URL with wildcard into a regex and match against the request URL
                urlMatches = Regex.IsMatch(requestUrl, ProxyUtils.PatternToRegex(mockResponse.Request.Url));
            }

            if (urlMatches && await HasMatchingBody(mockResponse, request) && IsNthRequest(mockResponse))
            {
                matchingMockResponse = mockResponse;
                break;
            }
        }

        if (matchingMockResponse is not null && matchingMockResponse.Request is not null)
        {
            _ = _appliedMocks.AddOrUpdate(matchingMockResponse.Request.Url, 1, (_, value) => ++value);
        }

        return matchingMockResponse;
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

    private HttpResponseMessage ProcessMockResponseInternal(HttpRequestMessage request, MockResponse matchingResponse, RequestId requestId)
    {
        string? body = null;
        var requestDate = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);
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

        // Check for rate limiting headers from RateLimitingPlugin using new storage API
        var requestData = proxyStorage.GetRequestData(requestId);
        if (requestData.TryGetValue(nameof(Behavior.RateLimitingPlugin), out var pluginData) &&
            pluginData is List<MockResponseHeader> rateLimitingHeaders)
        {
            ProxyUtils.MergeHeaders(headers, rateLimitingHeaders);
        }

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
                    ProcessMockResponse(ref bodyBytes, headers, request, matchingResponse);
                    var response = new HttpResponseMessage(statusCode);
                    foreach (var header in headers)
                    {
                        _ = response.Headers.TryAddWithoutValidation(header.Name, header.Value);
                    }
                    response.Content = new ByteArrayContent(bodyBytes);
                    Logger.LogRequest($"{matchingResponse.Response.StatusCode ?? 200} {matchingResponse.Request?.Url}", MessageType.Mocked, request);
                    return response;
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

        ProcessMockResponse(ref body, headers, request, matchingResponse);

        var httpResponse = new HttpResponseMessage(statusCode);
        foreach (var header in headers)
        {
            _ = httpResponse.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        if (!string.IsNullOrEmpty(body))
        {
            httpResponse.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        Logger.LogRequest($"{matchingResponse.Response?.StatusCode ?? 200} {matchingResponse.Request?.Url}", MessageType.Mocked, request);
        return httpResponse;
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

    private static async Task<bool> HasMatchingBody(MockResponse mockResponse, HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get)
        {
            // GET requests don't have a body so we can't match on it
            return true;
        }

        if (mockResponse.Request?.BodyFragment is null)
        {
            // no body fragment to match on
            return true;
        }

        if (request.Content is null)
        {
            // mock defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        var requestBody = await request.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(requestBody))
        {
            // mock defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        return requestBody.Contains(mockResponse.Request.BodyFragment, StringComparison.OrdinalIgnoreCase);
    }
}
