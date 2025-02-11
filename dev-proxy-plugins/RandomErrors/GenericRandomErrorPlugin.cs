// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.Net;
using System.Text.Json;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Microsoft.DevProxy.Plugins.Behavior;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Microsoft.DevProxy.Plugins.RandomErrors;
internal enum GenericRandomErrorFailMode
{
    Throttled,
    Random,
    PassThru
}

public class GenericRandomErrorConfiguration
{
    public string? ErrorsFile { get; set; }
    public int RetryAfterInSeconds { get; set; } = 5;
    public IEnumerable<GenericErrorResponse> Errors { get; set; } = [];
}

public class GenericRandomErrorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private readonly GenericRandomErrorConfiguration _configuration = new();
    private GenericErrorResponsesLoader? _loader = null;

    public override string Name => nameof(GenericRandomErrorPlugin);

    private readonly Random _random = new();

    // uses config to determine if a request should be failed
    private GenericRandomErrorFailMode ShouldFail() => _random.Next(1, 100) <= Context.Configuration.Rate ? GenericRandomErrorFailMode.Random : GenericRandomErrorFailMode.PassThru;

    private void FailResponse(ProxyRequestArgs e)
    {
        var matchingResponse = GetMatchingErrorResponse(e.Session.HttpClient.Request);
        if (matchingResponse is not null &&
            matchingResponse.Responses is not null)
        {
            // pick a random error response for the current request
            var error = matchingResponse.Responses.ElementAt(_random.Next(0, matchingResponse.Responses.Length));
            UpdateProxyResponse(e, error);
        }
        else
        {
            Logger.LogRequest("No matching error response found", MessageType.Skipped, new LoggingContext(e.Session));
        }
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? _configuration.RetryAfterInSeconds : 0, "Retry-After");
    }

    private GenericErrorResponse? GetMatchingErrorResponse(Request request)
    {
        if (_configuration.Errors is null ||
            !_configuration.Errors.Any())
        {
            return null;
        }

        var errorResponse = _configuration.Errors.FirstOrDefault(errorResponse =>
        {
            if (errorResponse.Request is null) return false;
            if (errorResponse.Responses is null) return false;

            if (errorResponse.Request.Method != request.Method) return false;
            if (errorResponse.Request.Url == request.Url &&
                HasMatchingBody(errorResponse, request))
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!errorResponse.Request.Url.Contains('*'))
            {
                return false;
            }

            // turn mock URL with wildcard into a regex and match against the request URL
            var errorResponseUrlRegex = Regex.Escape(errorResponse.Request.Url).Replace("\\*", ".*");
            return Regex.IsMatch(request.Url, $"^{errorResponseUrlRegex}$") &&
                HasMatchingBody(errorResponse, request);
        });

        return errorResponse;
    }

    private static bool HasMatchingBody(GenericErrorResponse errorResponse, Request request)
    {
        if (request.Method == "GET")
        {
            // GET requests don't have a body so we can't match on it
            return true;
        }

        if (errorResponse.Request?.BodyFragment is null)
        {
            // no body fragment to match on
            return true;
        }

        if (!request.HasBody || string.IsNullOrEmpty(request.BodyString))
        {
            // error response defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        return request.BodyString.Contains(errorResponse.Request.BodyFragment, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateProxyResponse(ProxyRequestArgs e, GenericErrorResponseResponse error)
    {
        SessionEventArgs session = e.Session;
        Request request = session.HttpClient.Request;
        var headers = new List<GenericErrorResponseHeader>();
        if (error.Headers is not null)
        {
            headers.AddRange(error.Headers);
        }

        if (error.StatusCode == (int)HttpStatusCode.TooManyRequests &&
            error.Headers is not null &&
            error.Headers.FirstOrDefault(h => h.Name == "Retry-After" || h.Name == "retry-after")?.Value == "@dynamic")
        {
            var retryAfterDate = DateTime.Now.AddSeconds(_configuration.RetryAfterInSeconds);
            if (!e.GlobalData.ContainsKey(RetryAfterPlugin.ThrottledRequestsKey))
            {
                e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, new List<ThrottlerInfo>());
            }
            var throttledRequests = e.GlobalData[RetryAfterPlugin.ThrottledRequestsKey] as List<ThrottlerInfo>;
            throttledRequests?.Add(new ThrottlerInfo(BuildThrottleKey(request), ShouldThrottle, retryAfterDate));
            // replace the header with the @dynamic value with the actual value
            var h = headers.First(h => h.Name == "Retry-After" || h.Name == "retry-after");
            headers.Remove(h);
            headers.Add(new("Retry-After", _configuration.RetryAfterInSeconds.ToString()));
        }

        var statusCode = (HttpStatusCode)(error.StatusCode ?? 400);
        var body = error.Body is null ? string.Empty : JsonSerializer.Serialize(error.Body, ProxyUtils.JsonSerializerOptions);
        // we get a JSON string so need to start with the opening quote
        if (body.StartsWith("\"@"))
        {
            // we've got a mock body starting with @-token which means we're sending
            // a response from a file on disk
            // if we can read the file, we can immediately send the response and
            // skip the rest of the logic in this method
            // remove the surrounding quotes and the @-token
            var filePath = Path.Combine(Path.GetDirectoryName(_configuration.ErrorsFile) ?? "", ProxyUtils.ReplacePathTokens(body.Trim('"').Substring(1)));
            if (!File.Exists(filePath))
            {
                Logger.LogError("File {filePath} not found. Serving file path in the mock response", (string?)filePath);
                session.GenericResponse(body, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));
            }
            else
            {
                var bodyBytes = File.ReadAllBytes(filePath);
                session.GenericResponse(bodyBytes, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));
            }
        }
        else
        {
            session.GenericResponse(body, statusCode, headers.Select(h => new HttpHeader(h.Name, h.Value)));
        }
        e.ResponseState.HasBeenSet = true;
        Logger.LogRequest($"{error.StatusCode} {statusCode}", MessageType.Chaos, new LoggingContext(e.Session));
    }

    // throttle requests per host
    private static string BuildThrottleKey(Request r) => r.RequestUri.Host;

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        _configuration.ErrorsFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.ErrorsFile ?? string.Empty), Path.GetDirectoryName(Context.Configuration.ConfigFile ?? string.Empty) ?? string.Empty);

        _loader = new GenericErrorResponsesLoader(Logger, _configuration);

        PluginEvents.Init += OnInit;
        PluginEvents.BeforeRequest += OnRequestAsync;
    }

    private void OnInit(object? sender, InitArgs e)
    {
        _loader?.InitResponsesWatcher();
    }

    private Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (UrlsToWatch is null ||
            !e.ShouldExecute(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var failMode = ShouldFail();

        if (failMode == GenericRandomErrorFailMode.PassThru && Context.Configuration?.Rate != 100)
        {
            Logger.LogRequest("Pass through", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        FailResponse(e);

        return Task.CompletedTask;
    }
}
