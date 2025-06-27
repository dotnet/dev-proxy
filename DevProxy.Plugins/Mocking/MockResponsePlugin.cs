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
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v0.29.2/mockresponseplugin.mocksfile.schema.json";
}

public class MockResponsePlugin(
    ILogger<MockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<MockResponseConfiguration>(
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

    public override string Name => nameof(MockResponsePlugin);

    public override async Task InitializeAsync(InitArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e);

        _loader = ActivatorUtilities.CreateInstance<MockResponsesLoader>(e.ServiceProvider, Configuration);
    }

    public override Option[] GetOptions()
    {
        var _noMocks = new Option<bool?>("--no-mocks", ["-n"])
        {
            Description = "Disable loading mock requests"
        };

        var _mocksFile = new Option<string?>("--mocks-file")
        {
            Description = "Provide a file populated with mock responses"
        };

        return [_noMocks, _mocksFile];
    }

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var parseResult = e.ParseResult;

        // allow disabling of mocks as a command line option
        var noMocks = parseResult.GetValueForOption<bool?>(_noMocksOptionName, e.Options);
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
        var mocksFile = parseResult.GetValueForOption<string?>(_mocksFileOptionName, e.Options);
        if (mocksFile is not null)
        {
            Configuration.MocksFile = mocksFile;
        }

        Configuration.MocksFile = ProxyUtils.GetFullPath(Configuration.MocksFile, _proxyConfiguration.ConfigFile);

        // load the responses from the configured mocks file
        _loader?.InitFileWatcher();

        ValidateMocks();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e)
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
}
