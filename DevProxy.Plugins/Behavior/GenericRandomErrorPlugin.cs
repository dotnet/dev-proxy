// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine.Parsing;
using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace DevProxy.Plugins.Behavior;

enum GenericRandomErrorFailMode
{
    Throttled,
    Random,
    PassThru
}

public sealed class GenericRandomErrorConfiguration
{
    public IEnumerable<GenericErrorResponse> Errors { get; set; } = [];
    public string ErrorsFile { get; set; } = "errors.json";
    public int Rate { get; set; } = 50;
    public int RetryAfterInSeconds { get; set; } = 5;
}

public sealed class GenericRandomErrorPlugin(
    HttpClient httpClient,
    ILogger<GenericRandomErrorPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection,
    IProxyStorage proxyStorage) :
    BasePlugin<GenericRandomErrorConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _rateOptionName = "--failure-rate";

    private readonly Random _random = new();
    private GenericErrorResponsesLoader? _loader;

    public override string Name => nameof(GenericRandomErrorPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        Configuration.ErrorsFile = ProxyUtils.GetFullPath(Configuration.ErrorsFile, ProxyConfiguration.ConfigFile);

        _loader = ActivatorUtilities.CreateInstance<GenericErrorResponsesLoader>(e.ServiceProvider, Configuration);
        await _loader.InitFileWatcherAsync(cancellationToken);

        ValidateErrors();
    }

    public override Option[] GetOptions()
    {
        var _rateOption = new Option<int?>(_rateOptionName, "-f")
        {
            Description = "The percentage of chance that a request will fail",
            HelpName = "failure-rate"
        };
        _rateOption.Validators.Add((input) =>
        {
            try
            {
                var value = input.GetValue(_rateOption);
                if (value.HasValue && (value < 0 || value > 100))
                {
                    input.AddError($"{value} is not a valid failure rate. Specify a number between 0 and 100");
                }
            }
            catch (InvalidOperationException ex)
            {
                input.AddError(ex.Message);
            }
        });

        return [_rateOption];
    }

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var rate = e.ParseResult.GetValueOrDefault<int?>(_rateOptionName);
        if (rate is not null)
        {
            Configuration.Rate = rate.Value;
        }
    }

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
            return Task.FromResult(PluginResponse.Continue());
        }

        var failMode = ShouldFail();

        if (failMode == GenericRandomErrorFailMode.PassThru && Configuration.Rate != 100)
        {
            Logger.LogRequest("Pass through", MessageType.Skipped, args.Request, args.RequestId);
            return Task.FromResult(PluginResponse.Continue());
        }

        var response = FailResponse(args.Request, args.RequestId);
        if (response != null)
        {
            Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
            return Task.FromResult(PluginResponse.Respond(response));
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return Task.FromResult(PluginResponse.Continue());
    };

    // uses config to determine if a request should be failed
    private GenericRandomErrorFailMode ShouldFail() => _random.Next(1, 100) <= Configuration.Rate ? GenericRandomErrorFailMode.Random : GenericRandomErrorFailMode.PassThru;

    private HttpResponseMessage? FailResponse(HttpRequestMessage request, string requestId)
    {
        var matchingResponse = GetMatchingErrorResponse(request);
        if (matchingResponse is not null &&
            matchingResponse.Responses is not null)
        {
            // pick a random error response for the current request
            var error = matchingResponse.Responses.ElementAt(_random.Next(0, matchingResponse.Responses.Count()));
            return UpdateProxyResponse(request, error, requestId);
        }
        else
        {
            Logger.LogRequest("No matching error response found", MessageType.Skipped, request, requestId);
            return null;
        }
    }

    private ThrottlingInfo ShouldThrottle(HttpRequestMessage request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new(throttleKeyForRequest == throttlingKey ? Configuration.RetryAfterInSeconds : 0, "Retry-After");
    }

    private GenericErrorResponse? GetMatchingErrorResponse(HttpRequestMessage request)
    {
        if (Configuration.Errors is null ||
            !Configuration.Errors.Any())
        {
            return null;
        }

        var errorResponse = Configuration.Errors.FirstOrDefault(errorResponse =>
        {
            if (errorResponse.Request is null)
            {
                return false;
            }

            if (errorResponse.Responses is null)
            {
                return false;
            }

            if (errorResponse.Request.Method != request.Method.Method)
            {
                return false;
            }

            if (errorResponse.Request.Url == request.RequestUri?.ToString() &&
                HasMatchingBodyAsync(errorResponse, request).GetAwaiter().GetResult())
            {
                return true;
            }

            // check if the URL contains a wildcard
            // if it doesn't, it's not a match for the current request for sure
            if (!errorResponse.Request.Url.Contains('*', StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // turn mock URL with wildcard into a regex and match against the request URL
            var errorResponseUrlRegex = Regex.Escape(errorResponse.Request.Url).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            return request.RequestUri != null &&
                   Regex.IsMatch(request.RequestUri.ToString(), $"^{errorResponseUrlRegex}$") &&
                   HasMatchingBodyAsync(errorResponse, request).GetAwaiter().GetResult();
        });

        return errorResponse;
    }

    private HttpResponseMessage UpdateProxyResponse(HttpRequestMessage request, GenericErrorResponseResponse error, string requestId)
    {
        var headers = new List<GenericErrorResponseHeader>();
        if (error.Headers is not null)
        {
            headers.AddRange(error.Headers);
        }

        // Note: Global data handling for throttling is temporarily disabled
        // This needs to be addressed with a proper service for managing throttled requests
        // TODO: Implement proper throttling service for the new API
        if (error.StatusCode == (int)HttpStatusCode.TooManyRequests &&
            error.Headers is not null &&
            error.Headers.FirstOrDefault(h => h.Name is "Retry-After" or "retry-after")?.Value == "@dynamic")
        {
            var retryAfterDate = DateTime.Now.AddSeconds(Configuration.RetryAfterInSeconds);
            if (!proxyStorage.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out var value))
            {
                value = new List<ThrottlerInfo>();
                proxyStorage.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
            }
            var throttledRequests = value as List<ThrottlerInfo>;
            throttledRequests?.Add(new(BuildThrottleKey(request), ShouldThrottle, retryAfterDate));
            // replace the header with the @dynamic value with the actual value
            var h = headers.First(h => h.Name is "Retry-After" or "retry-after");
            _ = headers.Remove(h);
            headers.Add(new("Retry-After", Configuration.RetryAfterInSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        var statusCode = (HttpStatusCode)(error.StatusCode ?? 400);
        var body = error.Body is null ? string.Empty : JsonSerializer.Serialize(error.Body, ProxyUtils.JsonSerializerOptions);

        var response = new HttpResponseMessage(statusCode);

        // we get a JSON string so need to start with the opening quote
        if (body.StartsWith("\"@"))
        {
            // we've got a mock body starting with @-token which means we're sending
            // a response from a file on disk
            // if we can read the file, we can immediately send the response and
            // skip the rest of the logic in this method
            // remove the surrounding quotes and the @-token
            var filePath = Path.Combine(Path.GetDirectoryName(Configuration.ErrorsFile) ?? "", ProxyUtils.ReplacePathTokens(body.Trim('"').Substring(1)));
            if (!File.Exists(filePath))
            {
                Logger.LogError("File {FilePath} not found. Serving file path in the mock response", (string?)filePath);
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            else
            {
                var bodyBytes = File.ReadAllBytes(filePath);
                response.Content = new ByteArrayContent(bodyBytes);
            }
        }
        else
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // Add headers to response
        foreach (var header in headers)
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                // Content-Type header goes on the content, not the response
                if (response.Content != null)
                {
                    _ = response.Content.Headers.Remove("Content-Type");
                    response.Content.Headers.Add("Content-Type", header.Value);
                }
            }
            else
            {
                response.Headers.Add(header.Name, header.Value);
            }
        }

        Logger.LogRequest($"{error.StatusCode} {statusCode}", MessageType.Chaos, request, requestId);
        return response;
    }

    private void ValidateErrors()
    {
        Logger.LogDebug("Validating error responses");

        if (Configuration.Errors is null ||
            !Configuration.Errors.Any())
        {
            Logger.LogDebug("No error responses defined");
            return;
        }

        var unmatchedErrorUrls = new List<string>();

        foreach (var error in Configuration.Errors)
        {
            if (error.Request is null)
            {
                Logger.LogDebug("Error response is missing a request");
                continue;
            }

            if (string.IsNullOrEmpty(error.Request.Url))
            {
                Logger.LogDebug("Error response is missing a URL");
                continue;
            }

            if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, error.Request.Url, true))
            {
                unmatchedErrorUrls.Add(error.Request.Url);
            }
        }

        if (unmatchedErrorUrls.Count == 0)
        {
            Logger.LogDebug("All error response URLs are matched");
            return;
        }

        var suggestedWildcards = ProxyUtils.GetWildcardPatterns(unmatchedErrorUrls.AsReadOnly());
        Logger.LogWarning(
            "The following URLs in {ErrorsFile} don't match any URL to watch: {UnmatchedMocks}. Add the following URLs to URLs to watch: {UrlsToWatch}",
            Configuration.ErrorsFile,
            string.Join(", ", unmatchedErrorUrls),
            string.Join(", ", suggestedWildcards)
        );
    }

    private static async Task<bool> HasMatchingBodyAsync(GenericErrorResponse errorResponse, HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get)
        {
            // GET requests don't have a body so we can't match on it
            return true;
        }

        if (errorResponse.Request?.BodyFragment is null)
        {
            // no body fragment to match on
            return true;
        }

        if (request.Content == null)
        {
            // error response defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        var requestBody = await request.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(requestBody))
        {
            // error response defines a body fragment but the request has no body
            // so it can't match
            return false;
        }

        return requestBody.Contains(errorResponse.Request.BodyFragment, StringComparison.OrdinalIgnoreCase);
    }

    // throttle requests per host
    private static string BuildThrottleKey(HttpRequestMessage request) => request.RequestUri?.Host ?? "";
}
