﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft.Graph.DeveloperProxy {
    internal enum FailMode {
        Throttled,
        Random,
        PassThru
    }

    public class ChaosEngine {
        private int retryAfterInSeconds = 5;
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

        private readonly ProxyConfiguration _config;
        private readonly Random _random;
        private ProxyServer? _proxyServer;
        private ExplicitProxyEndPoint? _explicitEndPoint;
        private readonly Dictionary<string, DateTime> _throttledRequests;
        private readonly ConsoleColor _color;
        // lists of URLs to watch, used for intercepting requests
        private List<Regex> urlsToWatch = new List<Regex>();
        // lists of hosts to watch extracted from urlsToWatch,
        // used for deciding which URLs to decrypt for further inspection
        private List<Regex> hostsToWatch = new List<Regex>();

        public ChaosEngine(ProxyConfiguration config) {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.InitResponsesWatcher();

            _random = new Random();
            _throttledRequests = new Dictionary<string, DateTime>();
            if (_config.AllowedErrors.Any()) {
                foreach (string k in _methodStatusCode.Keys) {
                    _methodStatusCode[k] = _methodStatusCode[k].Where(e => _config.AllowedErrors.Any(a => (int)e == a)).ToArray();
                }
            }

            _color = Console.ForegroundColor;
        }

        public async Task Run(CancellationToken? cancellationToken) {
            if (!_config.UrlsToWatch.Any()) {
                Console.WriteLine("No URLs to watch configured. Please add URLs to watch in the appsettings.json config file.");
                return;
            }

            LoadUrlsToWatch();

            _proxyServer = new ProxyServer();

            _proxyServer.CertificateManager.CertificateStorage = new CertificateDiskCache();
            _proxyServer.BeforeRequest += OnRequest;
            _proxyServer.BeforeResponse += OnResponse;
            _proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            _proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;
            cancellationToken?.Register(OnCancellation);

            _explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, _config.Port, true);
            if (!RunTime.IsWindows) {
                // we need to change this to a value lower than 397
                // to avoid the ERR_CERT_VALIDITY_TOO_LONG error in Edge
                _proxyServer.CertificateManager.CertificateValidDays = 365;
                // we need to call it explicitly for non-Windows OSes because it's
                // a part of the SetAsSystemHttpProxy that works only on Windows
                _proxyServer.CertificateManager.EnsureRootCertificate();
            }

            // Fired when a CONNECT request is received
            _explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;

            _proxyServer.AddEndPoint(_explicitEndPoint);
            _proxyServer.Start();

            foreach (var endPoint in _proxyServer.ProxyEndPoints) {
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ",
                    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);
            }

            if (RunTime.IsWindows) {
                // Only explicit proxies can be set as system proxy!
                _proxyServer.SetAsSystemHttpProxy(_explicitEndPoint);
                _proxyServer.SetAsSystemHttpsProxy(_explicitEndPoint);
            }
            else {
                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Configure your operating system to use this proxy's port and address");
                Console.ForegroundColor = color;
            }

            // wait here (You can use something else as a wait function, I am using this as a demo)
            Console.WriteLine("Press CTRL+C to stop the Microsoft Graph Developer Proxy");
            Console.CancelKeyPress += Console_CancelKeyPress;
            // wait for the proxy to stop
            while (_proxyServer.ProxyRunning) { Thread.Sleep(10); }
        }

        // Convert strings from config to regexes.
        // From the list of URLs, extract host names and convert them to regexes.
        // We need this because before we decrypt a request, we only have access
        // to the host name, not the full URL.
        private void LoadUrlsToWatch() {
            foreach (var urlToWatch in _config.UrlsToWatch) {
                // add the full URL
                var urlToWatchRegexString = Regex.Escape(urlToWatch).Replace("\\*", ".*");
                urlsToWatch.Add(new Regex(urlToWatchRegexString, RegexOptions.Compiled | RegexOptions.IgnoreCase));

                // extract host from the URL
                var hostToWatch = "";
                if (urlToWatch.Contains("://")) {
                    // if the URL contains a protocol, extract the host from the URL
                    hostToWatch = urlToWatch.Split("://")[1].Substring(0, urlToWatch.Split("://")[1].IndexOf("/"));
                }
                else {
                    // if the URL doesn't contain a protocol,
                    // we assume the whole URL is a host name
                    hostToWatch = urlToWatch;
                }

                var hostToWatchRegexString = Regex.Escape(hostToWatch).Replace("\\*", ".*");
                // don't add the same host twice
                if (!hostsToWatch.Any(h => h.ToString() == hostToWatchRegexString)) {
                    hostsToWatch.Add(new Regex(hostToWatchRegexString, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                }
            }
        }

        private void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e) {
            StopProxy();
        }

        private void StopProxy() {
            // Unsubscribe & Quit
            try {
                if (_explicitEndPoint != null) {
                    _explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
                }

                if (_proxyServer != null) {
                    _proxyServer.BeforeRequest -= OnRequest;
                    _proxyServer.BeforeResponse -= OnResponse;
                    _proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
                    _proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

                    _proxyServer.Stop();
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"Exception: {ex.Message}");
            }
        }

        private void OnCancellation() {
            if (_explicitEndPoint is not null) {
                _explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
            }

            if (_proxyServer is not null) {
                _proxyServer.BeforeRequest -= OnRequest;
                _proxyServer.BeforeResponse -= OnResponse;
                _proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
                _proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

                _proxyServer.Stop();
            }
        }

        // uses config to determine if a request should be failed
        private FailMode ShouldFail(Request r) {
            string key = BuildThrottleKey(r);
            if (_throttledRequests.TryGetValue(key, out DateTime retryAfterDate)) {
                if (retryAfterDate > DateTime.Now) {
                    Console.Error.WriteLine($"Calling {r.Url} again before waiting for the Retry-After period. Request will be throttled");
                    // update the retryAfterDate to extend the throttling window to ensure that brute forcing won't succeed.
                    _throttledRequests[key] = retryAfterDate.AddSeconds(retryAfterInSeconds);
                    return FailMode.Throttled;
                }
                else {
                    // clean up expired throttled request and ensure that this request is passed through.
                    _throttledRequests.Remove(key);
                    return FailMode.PassThru;
                }
            }

            return _random.Next(1, 100) <= _config.FailureRate ? FailMode.Random : FailMode.PassThru;
        }

        async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e) {
            // Ensures that only the targeted Https domains are proxyied
            if (!ShouldDecryptRequest(e.HttpClient.Request.RequestUri.Host)) {
                e.DecryptSsl = false;
            }
        }

        async Task OnRequest(object sender, SessionEventArgs e) {
            var method = e.HttpClient.Request.Method.ToUpper();
            if (method is "POST" or "PUT" or "PATCH") {
                // Get/Set request body bytes
                byte[] bodyBytes = await e.GetRequestBody();
                e.SetRequestBody(bodyBytes);

                // Get/Set request body as string
                string bodyString = await e.GetRequestBodyAsString();
                e.SetRequestBodyString(bodyString);

                // store request
                // so that you can find it from response handler
                e.UserData = e.HttpClient.Request;
            }

            // Chaos happens only for requests which are not OPTIONS
            if (method is not "OPTIONS" && ShouldWatchRequest(e.HttpClient.Request.Url)) {
                Console.WriteLine($"saw a request: {e.HttpClient.Request.Method} {e.HttpClient.Request.Url}");
                HandleRequest(e);
            }
        }

        private void HandleRequest(SessionEventArgs e) {
            var responseComponents = ResponseComponents.Build();
            var matchingResponse = GetMatchingMockResponse(e.HttpClient.Request);
            if (matchingResponse is not null) {
                ProcessMockResponse(e, responseComponents, matchingResponse);
            }
            else {
                var failMode = ShouldFail(e.HttpClient.Request);

                if (WarnNoSelect(e.HttpClient.Request)) {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"\tWARNING: {BuildUseSelectMessage(e.HttpClient.Request)}");
                    Console.ForegroundColor = _color;
                }

                if (failMode == FailMode.PassThru && _config.FailureRate != 100) {
                    Console.WriteLine($"\tPassed through {e.HttpClient.Request.Url}");
                    return;
                }

                FailResponse(e, responseComponents, failMode);
                if (IsGraphRequest(e.HttpClient.Request) &&
                    !IsSdkRequest(e.HttpClient.Request)) {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Error.WriteLine($"\tTIP: {BuildUseSdkMessage(e.HttpClient.Request)}");
                    Console.ForegroundColor = _color;
                }
            }
            if (!responseComponents.ResponseIsComplete)
                UpdateProxyResponse(e, responseComponents, matchingResponse);
        }

        private static string BuildUseSdkMessage(Request r) => $"To handle API errors more easily, use the Graph SDK. More info at {GetMoveToSdkUrl(r)}";

        private static string BuildUseSelectMessage(Request r) => $"To improve performance of your application, use the $select parameter. More info at {GetSelectParameterGuidanceUrl(r)}";

        private void FailResponse(SessionEventArgs e, ResponseComponents r, FailMode failMode) {
            if (failMode == FailMode.Throttled) {
                r.ErrorStatus = HttpStatusCode.TooManyRequests;
            }
            else {
                // there's no matching mock response so pick a random response
                // for the current request method
                var methodStatusCodes = _methodStatusCode[e.HttpClient.Request.Method];
                r.ErrorStatus = methodStatusCodes[_random.Next(0, methodStatusCodes.Length)];
            }
        }

        private static bool IsSdkRequest(Request request) {
            return request.Headers.HeaderExists("SdkVersion");
        }

        private static bool IsGraphRequest(Request request) {
            return request.RequestUri.Host.Contains("graph", StringComparison.OrdinalIgnoreCase);
        }

        private static bool WarnNoSelect(Request request) {
            return IsGraphRequest(request) &&
                request.Method == "GET" &&
                !request.Url.Contains("$select", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMoveToSdkUrl(Request request) {
            // TODO: return language-specific guidance links based on the language detected from the User-Agent
            return "https://aka.ms/move-to-graph-js-sdk";
        }

        private static string GetSelectParameterGuidanceUrl(Request request) {
            return "https://learn.microsoft.com/graph/query-parameters#select-parameter";
        }

        private static void ProcessMockResponse(SessionEventArgs e, ResponseComponents responseComponents, ProxyMockResponse matchingResponse) {
            if (matchingResponse.ResponseCode is not null) {
                responseComponents.ErrorStatus = (HttpStatusCode)matchingResponse.ResponseCode;
            }

            if (matchingResponse.ResponseHeaders is not null) {
                foreach (var key in matchingResponse.ResponseHeaders.Keys) {
                    responseComponents.Headers.Add(new HttpHeader(key, matchingResponse.ResponseHeaders[key]));
                }
            }

            if (matchingResponse.ResponseBody is not null) {
                var bodyString = JsonSerializer.Serialize(matchingResponse.ResponseBody) as string;
                // we get a JSON string so need to start with the opening quote
                if (bodyString?.StartsWith("\"@") ?? false) {
                    // we've got a mock body starting with @-token which means we're sending
                    // a response from a file on disk
                    // if we can read the file, we can immediately send the response and
                    // skip the rest of the logic in this method
                    // remove the surrounding quotes and the @-token
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), bodyString.Trim('"').Substring(1));
                    if (!File.Exists(filePath)) {
                        Console.Error.WriteLine($"File {filePath} not found. Serving file path in the mock response");
                        responseComponents.Body = bodyString;
                    }
                    else {
                        if (e.HttpClient.Request.Headers.FirstOrDefault((HttpHeader h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is not null) {
                            responseComponents.Headers.Add(new HttpHeader("Access-Control-Allow-Origin", "*"));
                        }

                        var bodyBytes = File.ReadAllBytes(filePath);
                        e.GenericResponse(bodyBytes, responseComponents.ErrorStatus, responseComponents.Headers);
                        responseComponents.ResponseIsComplete = true;
                    }
                }
                else {
                    responseComponents.Body = bodyString;
                }
            }
        }

        private bool ShouldDecryptRequest(string hostName) {
            return hostsToWatch.Any(h => h.IsMatch(hostName));
        }

        private bool ShouldWatchRequest(string requestUrl) {
            return urlsToWatch.Any(u => u.IsMatch(requestUrl));
        }

        private ProxyMockResponse? GetMatchingMockResponse(Request request) {
            if (_config.NoMocks ||
                _config.Responses is null ||
                !_config.Responses.Any()) {
                return null;
            }

            var mockResponse = _config.Responses.FirstOrDefault(mockResponse => {
                if (mockResponse.Method != request.Method) return false;
                if (mockResponse.Url == request.Url) {
                    return true;
                }

                // check if the URL contains a wildcard
                // if it doesn't, it's not a match for the current request for sure
                if (!mockResponse.Url.Contains('*')) {
                    return false;
                }

                // turn mock URL with wildcard into a regex and match against the request URL
                var mockResponseUrlRegex = Regex.Escape(mockResponse.Url).Replace("\\*", ".*");
                return Regex.IsMatch(request.Url, mockResponseUrlRegex);
            });
            return mockResponse;
        }

        private void UpdateProxyResponse(SessionEventArgs e, ResponseComponents responseComponents, ProxyMockResponse? matchingResponse) {
            if (responseComponents.ErrorStatus == HttpStatusCode.TooManyRequests) {
                var retryAfterDate = DateTime.Now.AddSeconds(retryAfterInSeconds);
                _throttledRequests[BuildThrottleKey(e.HttpClient.Request)] = retryAfterDate;
                responseComponents.Headers.Add(new HttpHeader("Retry-After", retryAfterInSeconds.ToString()));
            }

            if (e.HttpClient.Request.Headers.FirstOrDefault((HttpHeader h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is not null) {
                responseComponents.Headers.Add(new HttpHeader("Access-Control-Allow-Origin", "*"));
                responseComponents.Headers.Add(new HttpHeader("Access-Control-Expose-Headers", "ETag, Location, Preference-Applied, Content-Range, request-id, client-request-id, ReadWriteConsistencyToken, SdkVersion, WWW-Authenticate, x-ms-client-gcc-tenant, Retry-After"));
            }

            if ((int)responseComponents.ErrorStatus >= 400 && string.IsNullOrEmpty(responseComponents.Body)) {
                responseComponents.Body = JsonSerializer.Serialize(new ErrorResponseBody(
                    new ErrorResponseError {
                        Code = new Regex("([A-Z])").Replace(responseComponents.ErrorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                        Message = BuildApiErrorMessage(e.HttpClient.Request),
                        InnerError = new ErrorResponseInnerError {
                            RequestId = responseComponents.RequestId,
                            Date = responseComponents.RequestDate
                        }
                    })
                );
            }
            Console.WriteLine($"\t{(matchingResponse is not null ? "Mocked" : "Failed")} {e.HttpClient.Request.Url} with {responseComponents.ErrorStatus}");
            e.GenericResponse(responseComponents.Body ?? string.Empty, responseComponents.ErrorStatus, responseComponents.Headers);
        }

        private string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(IsGraphRequest(r) ? (IsSdkRequest(r) ? "" : BuildUseSdkMessage(r)) : "")}";

        private string BuildThrottleKey(Request r) => $"{r.Method}-{r.Url}";

        // Modify response
        async Task OnResponse(object sender, SessionEventArgs e) {
            // read response headers
            var responseHeaders = e.HttpClient.Response.Headers;

            if (e.HttpClient.Request.Method is "GET" or "POST") {
                if (e.HttpClient.Response.StatusCode == 200) {
                    if (e.HttpClient.Response.ContentType is not null && e.HttpClient.Response.ContentType.Trim().ToLower().Contains("text/html")) {
                        byte[] bodyBytes = await e.GetResponseBody();
                        e.SetResponseBody(bodyBytes);

                        string body = await e.GetResponseBodyAsString();
                        e.SetResponseBodyString(body);
                    }
                }
            }

            if (e.UserData is not null) {
                // access request from UserData property where we stored it in RequestHandler
                var request = (Request)e.UserData;
            }
        }

        // Allows overriding default certificate validation logic
        Task OnCertificateValidation(object sender, CertificateValidationEventArgs e) {
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None) {
                e.IsValid = true;
            }

            return Task.CompletedTask;
        }

        // Allows overriding default client certificate selection logic during mutual authentication
        Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e) {
            // set e.clientCertificate to override
            return Task.CompletedTask;
        }
    }

    public class ResponseComponents {
        public string RequestId { get; } = Guid.NewGuid().ToString();
        public string RequestDate { get; } = DateTime.Now.ToString();
        public List<HttpHeader> Headers { get; } = new List<HttpHeader>
        {
            new HttpHeader("Cache-Control", "no-store"),
            new HttpHeader("x-ms-ags-diagnostic", ""),
            new HttpHeader("Strict-Transport-Security", "")
        };

        public string? Body { get; set; } = string.Empty;
        public HttpStatusCode ErrorStatus { get; set; } = HttpStatusCode.OK;
        public bool ResponseIsComplete { get; set; } = false;

        public static ResponseComponents Build() {
            var result = new ResponseComponents();
            result.Headers.Add(new HttpHeader("request-id", result.RequestId));
            result.Headers.Add(new HttpHeader("client-request-id", result.RequestId));
            result.Headers.Add(new HttpHeader("Date", result.RequestDate));
            return result;
        }
    }
}