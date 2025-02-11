// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Microsoft.DevProxy.Plugins.Behavior;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Mocks;

public class MockResponseConfiguration
{
    [JsonIgnore]
    public bool NoMocks { get; set; } = false;
    [JsonIgnore]
    public string MocksFile { get; set; } = "mocks.json";
    [JsonIgnore]
    public bool BlockUnmockedRequests { get; set; } = false;

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v0.24.0/mockresponseplugin.schema.json";
    public IEnumerable<MockResponse> Mocks { get; set; } = [];
}

public class MockResponsePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    protected MockResponseConfiguration _configuration = new();
    private MockResponsesLoader? _loader = null;
    private static readonly string _noMocksOptionName = "--no-mocks";
    private static readonly string _mocksFileOptionName = "--mocks-file";
    public override string Name => nameof(MockResponsePlugin);
    private IProxyConfiguration? _proxyConfiguration;
    // tracks the number of times a mock has been applied
    // used in combination with mocks that have an Nth property
    private readonly Dictionary<string, int> _appliedMocks = [];

    public override Option[] GetOptions()
    {
        var _noMocks = new Option<bool?>(_noMocksOptionName, "Disable loading mock requests")
        {
            ArgumentHelpName = "no mocks"
        };
        _noMocks.AddAlias("-n");

        var _mocksFile = new Option<string?>(_mocksFileOptionName, "Provide a file populated with mock responses")
        {
            ArgumentHelpName = "mocks file"
        };

        return [_noMocks, _mocksFile];
    }

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        _loader = new MockResponsesLoader(Logger, _configuration);

        PluginEvents.OptionsLoaded += OnOptionsLoaded;
        PluginEvents.BeforeRequest += OnRequestAsync;

        _proxyConfiguration = Context.Configuration;
    }

    private void OnOptionsLoaded(object? sender, OptionsLoadedArgs e)
    {
        InvocationContext context = e.Context;

        // allow disabling of mocks as a command line option
        var noMocks = context.ParseResult.GetValueForOption<bool?>(_noMocksOptionName, e.Options);
        if (noMocks.HasValue)
        {
            _configuration.NoMocks = noMocks.Value;
        }
        if (_configuration.NoMocks)
        {
            // mocks have been disabled. No need to continue
            return;
        }

        // update the name of the mocks file to load from if supplied
        var mocksFile = context.ParseResult.GetValueForOption<string?>(_mocksFileOptionName, e.Options);
        if (mocksFile is not null)
        {
            _configuration.MocksFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(mocksFile));
        }

        _configuration.MocksFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.MocksFile), Path.GetDirectoryName(_proxyConfiguration?.ConfigFile ?? string.Empty) ?? string.Empty);

        // load the responses from the configured mocks file
        _loader?.InitResponsesWatcher();
    }

    protected virtual Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        Request request = e.Session.HttpClient.Request;
        ResponseState state = e.ResponseState;
        if (_configuration.NoMocks)
        {
            Logger.LogRequest("Mocks disabled", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (UrlsToWatch is null ||
            !e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var matchingResponse = GetMatchingMockResponse(request);
        if (matchingResponse is not null)
        {
            ProcessMockResponseInternal(e, matchingResponse);
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }
        else if (_configuration.BlockUnmockedRequests)
        {
            ProcessMockResponseInternal(e, new MockResponse
            {
                Request = new()
                {
                    Url = request.Url,
                    Method = request.Method ?? ""
                },
                Response = new()
                {
                    StatusCode = 502,
                    Body = new GraphErrorResponseBody(new GraphErrorResponseError
                    {
                        Code = "Bad Gateway",
                        Message = $"No mock response found for {request.Method} {request.Url}"
                    })
                }
            });
            state.HasBeenSet = true;
            return Task.CompletedTask;
        }

        Logger.LogRequest("No matching mock response found", MessageType.Skipped, new LoggingContext(e.Session));
        return Task.CompletedTask;
    }

    private MockResponse? GetMatchingMockResponse(Request request)
    {
        if (_configuration.NoMocks ||
            _configuration.Mocks is null ||
            !_configuration.Mocks.Any())
        {
            return null;
        }

        var mockResponse = _configuration.Mocks.FirstOrDefault(mockResponse =>
        {
            if (mockResponse.Request is null) return false;

            if (mockResponse.Request.Method != request.Method) return false;
            if (mockResponse.Request.Url == request.Url &&
                HasMatchingBody(mockResponse, request) &&
                IsNthRequest(mockResponse))
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!mockResponse.Request.Url.Contains('*'))
            {
                return false;
            }

            //turn mock URL with wildcard into a regex and match against the request URL
            var mockResponseUrlRegex = Regex.Escape(mockResponse.Request.Url).Replace("\\*", ".*");
            return Regex.IsMatch(request.Url, $"^{mockResponseUrlRegex}$") &&
                HasMatchingBody(mockResponse, request) &&
                IsNthRequest(mockResponse);
        });

        if (mockResponse is not null && mockResponse.Request is not null)
        {
            if (!_appliedMocks.TryGetValue(mockResponse.Request.Url, out int value))
            {
                value = 0;
                _appliedMocks.Add(mockResponse.Request.Url, value);
            }
            _appliedMocks[mockResponse.Request.Url] = ++value;
        }

        return mockResponse;
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

    private bool IsNthRequest(MockResponse mockResponse)
    {
        if (mockResponse.Request?.Nth is null)
        {
            // mock doesn't define an Nth property so it always qualifies
            return true;
        }

        _appliedMocks.TryGetValue(mockResponse.Request.Url, out var nth);
        nth++;

        return mockResponse.Request.Nth == nth;
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

    private void ProcessMockResponseInternal(ProxyRequestArgs e, MockResponse matchingResponse)
    {
        string? body = null;
        string requestId = Guid.NewGuid().ToString();
        string requestDate = DateTime.Now.ToString();
        var headers = ProxyUtils.BuildGraphResponseHeaders(e.Session.HttpClient.Request, requestId, requestDate);
        HttpStatusCode statusCode = HttpStatusCode.OK;
        if (matchingResponse.Response?.StatusCode is not null)
        {
            statusCode = (HttpStatusCode)matchingResponse.Response.StatusCode;
        }

        if (matchingResponse.Response?.Headers is not null)
        {
            ProxyUtils.MergeHeaders(headers, matchingResponse.Response.Headers);
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
            if (bodyString?.StartsWith("\"@") ?? false)
            {
                // we've got a mock body starting with @-token which means we're sending
                // a response from a file on disk
                // if we can read the file, we can immediately send the response and
                // skip the rest of the logic in this method
                // remove the surrounding quotes and the @-token
                var filePath = Path.Combine(Path.GetDirectoryName(_configuration.MocksFile) ?? "", ProxyUtils.ReplacePathTokens(bodyString.Trim('"').Substring(1)));
                if (!File.Exists(filePath))
                {

                    Logger.LogError("File {filePath} not found. Serving file path in the mock response", filePath);
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
                headers.Remove(contentTypeHeader);
            }
        }
        ProcessMockResponse(ref body, headers, e, matchingResponse);
        e.Session.GenericResponse(body ?? string.Empty, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));

        Logger.LogRequest($"{matchingResponse.Response?.StatusCode ?? 200} {matchingResponse.Request?.Url}", MessageType.Mocked, new LoggingContext(e.Session));
    }
}
