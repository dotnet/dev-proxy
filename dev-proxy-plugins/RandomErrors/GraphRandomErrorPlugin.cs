// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Microsoft.DevProxy.Plugins.Behavior;

namespace Microsoft.DevProxy.Plugins.RandomErrors;
internal enum GraphRandomErrorFailMode
{
    Random,
    PassThru
}

public class GraphRandomErrorConfiguration
{
    public List<int> AllowedErrors { get; set; } = [];
    public int RetryAfterInSeconds { get; set; } = 5;
}

public class GraphRandomErrorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private static readonly string _allowedErrorsOptionName = "--allowed-errors";
    private readonly GraphRandomErrorConfiguration _configuration = new();

    public override string Name => nameof(GraphRandomErrorPlugin);

    private readonly Dictionary<string, HttpStatusCode[]> _methodStatusCode = new()
    {
        {
            "GET", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "POST", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PUT", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        },
        {
            "PATCH", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout
            }
        },
        {
            "DELETE", new[] {
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.InsufficientStorage
            }
        }
    };
    private readonly Random _random = new();

    // uses config to determine if a request should be failed
    private GraphRandomErrorFailMode ShouldFail(ProxyRequestArgs e) => _random.Next(1, 100) <= Context.Configuration.Rate ? GraphRandomErrorFailMode.Random : GraphRandomErrorFailMode.PassThru;

    private void FailResponse(ProxyRequestArgs e)
    {
        // pick a random error response for the current request method
        var methodStatusCodes = _methodStatusCode[e.Session.HttpClient.Request.Method ?? "GET"];
        var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];
        UpdateProxyResponse(e, errorStatus);
    }

    private void FailBatch(ProxyRequestArgs e)
    {
        var batchResponse = new GraphBatchResponsePayload();

        var batch = JsonSerializer.Deserialize<GraphBatchRequestPayload>(e.Session.HttpClient.Request.BodyString, ProxyUtils.JsonSerializerOptions);
        if (batch == null)
        {
            UpdateProxyBatchResponse(e, batchResponse);
            return;
        }

        var responses = new List<GraphBatchResponsePayloadResponse>();
        foreach (var request in batch.Requests)
        {
            try
            {
                // pick a random error response for the current request method
                var methodStatusCodes = _methodStatusCode[request.Method];
                var errorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];

                var response = new GraphBatchResponsePayloadResponse
                {
                    Id = request.Id,
                    Status = (int)errorStatus,
                    Body = new GraphBatchResponsePayloadResponseBody
                    {
                        Error = new GraphBatchResponsePayloadResponseBodyError
                        {
                            Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                            Message = "Some error was generated by the proxy.",
                        }
                    }
                };

                if (errorStatus == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterDate = DateTime.Now.AddSeconds(_configuration.RetryAfterInSeconds);
                    var requestUrl = ProxyUtils.GetAbsoluteRequestUrlFromBatch(e.Session.HttpClient.Request.RequestUri, request.Url);
                    var throttledRequests = e.GlobalData[RetryAfterPlugin.ThrottledRequestsKey] as List<ThrottlerInfo>;
                    throttledRequests?.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(requestUrl), ShouldThrottle, retryAfterDate));
                    response.Headers = new Dictionary<string, string> { { "Retry-After", _configuration.RetryAfterInSeconds.ToString() } };
                }

                responses.Add(response);
            }
            catch { }
        }
        batchResponse.Responses = [.. responses];

        UpdateProxyBatchResponse(e, batchResponse);
    }

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = GraphUtils.BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? _configuration.RetryAfterInSeconds : 0, "Retry-After");
    }

    private void UpdateProxyResponse(ProxyRequestArgs e, HttpStatusCode errorStatus)
    {
        SessionEventArgs session = e.Session;
        string requestId = Guid.NewGuid().ToString();
        string requestDate = DateTime.Now.ToString();
        Request request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);
        if (errorStatus == HttpStatusCode.TooManyRequests)
        {
            var retryAfterDate = DateTime.Now.AddSeconds(_configuration.RetryAfterInSeconds);
            if (!e.GlobalData.TryGetValue(RetryAfterPlugin.ThrottledRequestsKey, out object? value))
            {
                value = new List<ThrottlerInfo>();
                e.GlobalData.Add(RetryAfterPlugin.ThrottledRequestsKey, value);
            }

            var throttledRequests = value as List<ThrottlerInfo>;
            throttledRequests?.Add(new ThrottlerInfo(GraphUtils.BuildThrottleKey(request), ShouldThrottle, retryAfterDate));
            headers.Add(new("Retry-After", _configuration.RetryAfterInSeconds.ToString()));
        }

        string body = JsonSerializer.Serialize(new GraphErrorResponseBody(
            new GraphErrorResponseError
            {
                Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                Message = BuildApiErrorMessage(request),
                InnerError = new GraphErrorResponseInnerError
                {
                    RequestId = requestId,
                    Date = requestDate
                }
            }),
            ProxyUtils.JsonSerializerOptions
        );
        Logger.LogRequest($"{(int)errorStatus} {errorStatus}", MessageType.Chaos, new LoggingContext(e.Session));
        session.GenericResponse(body ?? string.Empty, errorStatus, headers.Select(h => new HttpHeader(h.Name, h.Value)));
    }

    private void UpdateProxyBatchResponse(ProxyRequestArgs ev, GraphBatchResponsePayload response)
    {
        // failed batch uses a fixed 424 error status code
        var errorStatus = HttpStatusCode.FailedDependency;

        SessionEventArgs session = ev.Session;
        string requestId = Guid.NewGuid().ToString();
        string requestDate = DateTime.Now.ToString();
        Request request = session.HttpClient.Request;
        var headers = ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate);

        string body = JsonSerializer.Serialize(response, ProxyUtils.JsonSerializerOptions);
        Logger.LogRequest($"{(int)errorStatus} {errorStatus}", MessageType.Chaos, new LoggingContext(ev.Session));
        session.GenericResponse(body, errorStatus, headers.Select(h => new HttpHeader(h.Name, h.Value)));
    }

    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : String.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";

    public override Option[] GetOptions()
    {
        var _allowedErrors = new Option<IEnumerable<int>>(_allowedErrorsOptionName, "List of errors that Dev Proxy may produce")
        {
            ArgumentHelpName = "allowed errors",
            AllowMultipleArgumentsPerToken = true
        };
        _allowedErrors.AddAlias("-a");

        return [_allowedErrors];
    }

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);
        PluginEvents.OptionsLoaded += OnOptionsLoaded;
        PluginEvents.BeforeRequest += OnRequestAsync;
    }

    private void OnOptionsLoaded(object? sender, OptionsLoadedArgs e)
    {
        InvocationContext context = e.Context;

        // Configure the allowed errors
        var allowedErrors = context.ParseResult.GetValueForOption<IEnumerable<int>?>(_allowedErrorsOptionName, e.Options);
        if (allowedErrors?.Any() ?? false)
            _configuration.AllowedErrors = allowedErrors.ToList();

        if (_configuration.AllowedErrors.Count != 0)
        {
            foreach (string k in _methodStatusCode.Keys)
            {
                _methodStatusCode[k] = _methodStatusCode[k].Where(e => _configuration.AllowedErrors.Any(a => (int)e == a)).ToArray();
            }
        }
    }

    private Task OnRequestAsync(object? sender, ProxyRequestArgs e)
    {
        var state = e.ResponseState;
        if (state.HasBeenSet)
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


        var failMode = ShouldFail(e);
        if (failMode == GraphRandomErrorFailMode.PassThru && Context.Configuration.Rate != 100)
        {
            Logger.LogRequest("Pass through", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (ProxyUtils.IsGraphBatchUrl(e.Session.HttpClient.Request.RequestUri))
        {
            FailBatch(e);
        }
        else
        {
            FailResponse(e);
        }
        state.HasBeenSet = true;

        return Task.CompletedTask;
    }
}
