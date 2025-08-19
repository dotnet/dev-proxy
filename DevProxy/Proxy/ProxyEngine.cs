// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Unobtanium.Web.Proxy;
using Unobtanium.Web.Proxy.Events;

namespace DevProxy.Proxy;

enum ToggleSystemProxyAction
{
    On,
    Off
}

sealed class ProxyEngine(
    IEnumerable<IPlugin> plugins,
    IProxyConfiguration proxyConfiguration,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStateController proxyController,
    ILogger<ProxyEngine> logger,
    ProxyServerEvents proxyEvents,
    ICertificateManager certificateManager) : BackgroundService, IDisposable
{
    internal const string ACTIVITY_SOURCE_NAME = "DevProxy.Proxy.ProxyEngine";
    public static readonly ActivitySource ActivitySource = new(ACTIVITY_SOURCE_NAME);
    private readonly IEnumerable<IPlugin> _plugins = plugins;
    private readonly ILogger _logger = logger;
    private readonly IProxyConfiguration _config = proxyConfiguration;

    //internal static ProxyServer ProxyServer { get; private set; }
    //private ExplicitProxyEndPoint? _explicitEndPoint;
    // lists of URLs to watch, used for intercepting requests
    private readonly ISet<UrlToWatch> _urlsToWatch = urlsToWatch;
    // lists of hosts to watch extracted from urlsToWatch,
    // used for deciding which URLs to decrypt for further inspection
    private readonly HashSet<UrlToWatch> _hostsToWatch = [];
    private readonly IProxyStateController _proxyController = proxyController;
    // Dictionary for plugins to store data between requests
    // the key is HashObject of the SessionEventArgs object
    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _pluginData = [];
    private InactivityTimer? _inactivityTimer;
    private CancellationToken? _cancellationToken;

    //public static X509Certificate2? Certificate => proxyServer?.CertificateManager.RootCertificate;

    //private ExceptionHandler ExceptionHandler => ex => _logger.LogError(ex, "An error occurred in a plugin");

    //static ProxyEngine()
    //{
    //    ProxyServer = new();
    //    ProxyServer.CertificateManager.PfxFilePath = Environment.GetEnvironmentVariable("DEV_PROXY_CERT_PATH") ?? string.Empty;
    //    ProxyServer.CertificateManager.RootCertificateName = "Dev Proxy CA";
    //    ProxyServer.CertificateManager.CertificateStorage = new CertificateDiskCache();
    //    // we need to change this to a value lower than 397
    //    // to avoid the ERR_CERT_VALIDITY_TOO_LONG error in Edge
    //    ProxyServer.CertificateManager.CertificateValidDays = 365;

    //    using var joinableTaskContext = new JoinableTaskContext();
    //    var joinableTaskFactory = new JoinableTaskFactory(joinableTaskContext);
    //    _ = joinableTaskFactory.Run(async () => await ProxyServer.CertificateManager.LoadOrCreateRootCertificateAsync());
    //}

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cancellationToken = stoppingToken;

        Debug.Assert(proxyEvents is not null, "Proxy server is not initialized");

        if (!_urlsToWatch.Any())
        {
            _logger.LogError("No URLs to watch configured. Please add URLs to watch in the devproxyrc.json config file.");
            return;
        }

        LoadHostNamesFromUrls();

        // TODO: Handle replacement of BeforeRequest
        //proxyServer.BeforeRequest += OnRequestAsync;
        proxyEvents.OnRequest += OnRequestAsync;

        // TODO: Handle removal of BeforeResponse
        //proxyServer.BeforeResponse += OnBeforeResponseAsync;

        // TODO: Handle replacement of AfterResponse
        //proxyServer.AfterResponse += OnAfterResponseAsync;
        proxyEvents.OnResponse += OnResponseAsync;

        //proxyServer.ServerCertificateValidationCallback += OnCertificateValidationAsync;
        //proxyServer.ClientCertificateSelectionCallback += OnCertificateSelectionAsync;

        // Endpoint is configured in IServiceCollectionExtensions.AddProxyConfiguration
        //var ipAddress = string.IsNullOrEmpty(_config.IPAddress) ? IPAddress.Any : IPAddress.Parse(_config.IPAddress);
        //_explicitEndPoint = new(ipAddress, _config.Port, true);

        // TODO: Implement process validation
        // Fired when a CONNECT request is received
        //_explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequestAsync;
        // This is superceeded by:
        proxyEvents.ShouldDecryptNewConnection = (host, client, cts) => Task.FromResult(IsProxiedHost(host));// || IsProxiedProcess(...));
        if (_config.InstallCert)
        {
            _ = await certificateManager.GetRootCertificateAsync(false, stoppingToken);
            // TODO: Execute code to trust certificate
        }
        else
        {
            // TODO: Remove this code, happens automatically
            //_explicitEndPoint.GenericCertificate = await proxyServer
            //    .CertificateManager
            //    .LoadRootCertificateAsync(stoppingToken);
        }

        //proxyServer.AddEndPoint(_explicitEndPoint);
        //await proxyServer.StartAsync(cancellationToken: stoppingToken);

        // run first-run setup on macOS
        FirstRunSetup();

        //ExplicitProxyEndPoint? explicitProxyEndPoint = null;

        //foreach (var endPoint in proxyServer.ProxyEndPoints)
        //{
        //    _logger.LogInformation("Dev Proxy listening on {IPAddress}:{Port}...", endPoint.IpAddress, endPoint.Port);
        //    if (explicitProxyEndPoint is null && endPoint is ExplicitProxyEndPoint explicitProxyEnd)
        //    {
        //        explicitProxyEndPoint = explicitProxyEnd;
        //    }
        //}

        if (_config.AsSystemProxy)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                //TODO: Implement Windows system proxy toggle
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ToggleSystemProxy(ToggleSystemProxyAction.On, _config.IPAddress, _config.Port);
            }
            else
            {
                _logger.LogWarning("Configure your operating system to use this proxy's port and address {IPAddress}:{Port}", _config.IPAddress, _config.Port);
            }
        }
        else
        {
            _logger.LogInformation("Configure your application to use this proxy's port and address");
        }

        var isInteractive = !Console.IsInputRedirected &&
            Environment.GetEnvironmentVariable("CI") is null;

        if (isInteractive)
        {
            // only print hotkeys when they can be used
            PrintHotkeys();
        }

        if (_config.Record)
        {
            StartRecording();
        }

        if (_config.TimeoutSeconds.HasValue)
        {
            _inactivityTimer = new(_config.TimeoutSeconds.Value, _proxyController.StopProxy);
        }

        if (!isInteractive)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (!Console.KeyAvailable)
                {
                    await Task.Delay(10, stoppingToken);
                }

                await ReadKeysAsync(stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            throw;
        }
    }

    private void FirstRunSetup()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            _config.NoFirstRun ||
            !HasRunFlag.CreateIfMissing() ||
            !_config.InstallCert)
        {
            return;
        }

        var bashScriptPath = Path.Join(ProxyUtils.AppFolder, "trust-cert.sh");
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            Arguments = bashScriptPath,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        using var process = new Process() { StartInfo = startInfo };
        _ = process.Start();
        process.WaitForExit();
    }

    private async Task ReadKeysAsync(CancellationToken cancellationToken)
    {
        var key = Console.ReadKey(true).Key;
#pragma warning disable IDE0010
        switch (key)
#pragma warning restore IDE0010
        {
            case ConsoleKey.R:
                StartRecording();
                break;
            case ConsoleKey.S:
                await StopRecordingAsync(cancellationToken);
                break;
            case ConsoleKey.C:
                Console.Clear();
                PrintHotkeys();
                break;
            case ConsoleKey.W:
                await _proxyController.MockRequestAsync(cancellationToken);
                break;
        }
    }

    private void StartRecording()
    {
        if (_proxyController.ProxyState.IsRecording)
        {
            return;
        }

        _proxyController.StartRecording();
    }

    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (!_proxyController.ProxyState.IsRecording)
        {
            return;
        }

        await _proxyController.StopRecordingAsync(cancellationToken);
    }

    // Convert strings from config to regexes.
    // From the list of URLs, extract host names and convert them to regexes.
    // We need this because before we decrypt a request, we only have access
    // to the host name, not the full URL.
    private void LoadHostNamesFromUrls()
    {
        foreach (var urlToWatch in _urlsToWatch)
        {
            // extract host from the URL
            var urlToWatchPattern = Regex.Unescape(urlToWatch.Url.ToString())
                .Trim('^', '$')
                .Replace(".*", "*", StringComparison.OrdinalIgnoreCase);
            string hostToWatch;
            if (urlToWatchPattern.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                // if the URL contains a protocol, extract the host from the URL
                var urlChunks = urlToWatchPattern.Split("://");
                var slashPos = urlChunks[1].IndexOf('/', StringComparison.OrdinalIgnoreCase);
                hostToWatch = slashPos < 0 ? urlChunks[1] : urlChunks[1][..slashPos];
            }
            else
            {
                // if the URL doesn't contain a protocol,
                // we assume the whole URL is a host name
                hostToWatch = urlToWatchPattern;
            }

            // remove port number if present
            var portPos = hostToWatch.IndexOf(':', StringComparison.OrdinalIgnoreCase);
            if (portPos > 0)
            {
                hostToWatch = hostToWatch[..portPos];
            }

            var hostToWatchRegexString = Regex.Escape(hostToWatch).Replace("\\*", ".*", StringComparison.OrdinalIgnoreCase);
            Regex hostRegex = new($"^{hostToWatchRegexString}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            // don't add the same host twice
            if (!_hostsToWatch.Any(h => h.Url.ToString() == hostRegex.ToString()))
            {
                _ = _hostsToWatch.Add(new(hostRegex, urlToWatch.Exclude));
            }
        }
    }

    private void StopProxy()
    {
        // Unsubscribe & Quit
        try
        {
            //if (_explicitEndPoint != null)
            //{
            //    _explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequestAsync;
            //}
            // proxyServer is stopped automatically when the service is stopped

            _inactivityTimer?.Stop();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && _config.AsSystemProxy)
            {
                ToggleSystemProxy(ToggleSystemProxyAction.Off);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while stopping the proxy");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopRecordingAsync(cancellationToken);
        StopProxy();

        await base.StopAsync(cancellationToken);
    }

    private bool IsProxiedProcess(ClientDetails clientDetails)
    {
        // If no process names or IDs are specified, we proxy all processes
        if (!_config.WatchPids.Any() &&
            !_config.WatchProcessNames.Any())
        {
            return true;
        }

        var processId = GetProcessId(clientDetails);
        if (processId == -1)
        {
            return false;
        }

        if (_config.WatchPids.Contains(processId))
        {
            return true;
        }

        if (_config.WatchProcessNames.Any())
        {
            var processName = Process.GetProcessById(processId).ProcessName;
            if (_config.WatchProcessNames.Contains(processName))
            {
                return true;
            }
        }

        return false;
    }

    async Task<RequestEventResponse> OnRequestAsync(object _, RequestEventArguments requestEventArguments, CancellationToken cancellationToken)
    {
        _inactivityTimer?.Reset();
        if (IsProxiedHost(requestEventArguments.Request.RequestUri!.Host) &&
            IsIncludedByHeaders(requestEventArguments.Request.Headers))
        {
            if (!_pluginData.TryAdd(requestEventArguments.RequestId, []))
            {
                throw new InvalidOperationException($"Unable to initialize the plugin data storage for hash key {requestEventArguments.RequestId}");
            }

            if (!ProxyUtils.MatchesUrlToWatch(_urlsToWatch, requestEventArguments.Request.RequestUri.AbsoluteUri))
            {
                return RequestEventResponse.ContinueResponse();
            }

            if (!_pluginData.TryAdd(requestEventArguments.RequestId, []))
            {
                // Throwing here will break the request....
                throw new InvalidOperationException($"Unable to initialize the plugin data storage for hash key {requestEventArguments.RequestId}");
            }

            using var scope = _logger.BeginRequestScope(requestEventArguments.Request.Method, requestEventArguments.Request.RequestUri, requestEventArguments.RequestId);


            //var loggingContext = new LoggingContext(e);
            _logger.LogRequest($"{requestEventArguments.Request.Method} {requestEventArguments.Request.RequestUri}", MessageType.InterceptedRequest, requestEventArguments.Request);
            _logger.LogRequest($"{DateTimeOffset.UtcNow}", MessageType.Timestamp, requestEventArguments.Request);

            return await HandleRequestAsync(requestEventArguments, cancellationToken);
        }

        return RequestEventResponse.ContinueResponse();
    }

    private async Task<RequestEventResponse> HandleRequestAsync(RequestEventArguments arguments, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken ?? CancellationToken.None);

        // Plugins that don't modify the request but log it
        // can be called in parallel, because they don't affect each other.
        var logPlugins = _plugins.Where(p => p.Enabled && p.OnRequestLogAsync is not null);
        if (logPlugins.Any())
        {
            var logArguments = new Abstractions.Models.RequestArguments(arguments.Request, arguments.RequestId);
            // Call OnRequestLogAsync for all plugins at the same time and wait for all of them to complete
            var logTasks = logPlugins
                .Select(plugin => plugin.OnRequestLogAsync!(logArguments, cts.Token))
                .ToArray();
            try
            {
                await Task.WhenAll(logTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in a plugin while logging request {RequestMethod} {RequestUrl}",
                    arguments.Request.Method, arguments.Request.RequestUri);
            }
        }

        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        foreach (var plugin in _plugins
            .Where(p =>
                p.Enabled
                && p.OnRequestAsync is not null)) // Only plugins that have OnRequestAsync defined, maybe pre-select matches based on url?
        {
            cts.Token.ThrowIfCancellationRequested();
            try
            {
                var result = await plugin.OnRequestAsync!(new Abstractions.Models.RequestArguments(arguments.Request, arguments.RequestId), cts.Token);
                if (result is not null)
                {
                    if (result.Request is not null)
                    {
                        request = result.Request;
                        // TODO: Decide what to do in this case, continue processing or return the request?
                    }
                    else if (result.Response is not null)
                    {
                        response = result.Response;
                        // Plugins no longer have to check if the response is already been set.
                        // If a plugin sets a response, it is expected to be the final response.
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in plugin {PluginName} while processing request {RequestMethod} {RequestUrl}",
                    plugin.Name, arguments.Request.Method, arguments.Request.RequestUri);

            }
        }

        // We only need to set the proxy header if the proxy has not set a response and the request is going to be sent to the target.
        if (response is not null)
        {
            _ = _pluginData.Remove(arguments.RequestId, out _);
            return RequestEventResponse.EarlyResponse(response);
        }
        else if (request is not null)
        {
            // If the request is modified, we need to add the Via header
            AddProxyHeader(request);
            // We can return the request to be sent to the target
            return RequestEventResponse.ModifyRequest(request);
        }
        // If no plugins modified the request, we add the Via header to the original request
        AddProxyHeader(arguments.Request);
        return RequestEventResponse.ModifyRequest(arguments.Request);
    }

    private bool IsProxiedHost(string hostName)
    {
        var urlMatch = _hostsToWatch.FirstOrDefault(h => h.Url.IsMatch(hostName));
        return urlMatch is not null && !urlMatch.Exclude;
    }

    private bool IsIncludedByHeaders(HttpRequestHeaders requestHeaders)
    {
        if (_config.FilterByHeaders is null)
        {
            return true;
        }

        foreach (var header in _config.FilterByHeaders)
        {
            _logger.LogDebug("Checking header {Header} with value {Value}...",
                header.Name,
                string.IsNullOrEmpty(header.Value) ? "(any)" : header.Value
            );

            if (requestHeaders.Contains(header.Name))
            {
                if (string.IsNullOrEmpty(header.Value))
                {
                    _logger.LogDebug("Request has header {Header}", header.Name);
                    return true;
                }

                if (requestHeaders.Any(h => h.Key.Equals(header.Name, StringComparison.OrdinalIgnoreCase) && (h.Value.ToString()?.Equals(header.Value, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    _logger.LogDebug("Request header {Header} contains value {Value}", header.Name, header.Value);
                    return true;
                }
            }
            else
            {
                _logger.LogDebug("Request doesn't have header {Header}", header.Name);
            }
        }

        _logger.LogDebug("Request doesn't match any header filter. Ignoring");
        return false;
    }

    //// Modify response
    //// OnBeforeResponseAsync is no longer supported, where was this used for?
    //async Task OnBeforeResponseAsync(object sender, SessionEventArgs e)
    //{
    //    // read response headers
    //    if (IsProxiedHost(e.HttpClient.Request.RequestUri.Host))
    //    {
    //        var proxyResponseArgs = new ProxyResponseArgs(e, new())
    //        {
    //            SessionData = _pluginData[e.GetHashCode()],
    //            GlobalData = _proxyController.ProxyState.GlobalData
    //        };
    //        if (!proxyResponseArgs.HasRequestUrlMatch(_urlsToWatch))
    //        {
    //            return;
    //        }

    //        using var scope = _logger.BeginScope(e.HttpClient.Request.Method ?? "", e.HttpClient.Request.Url, e.GetHashCode());

    //        // necessary to make the response body available to plugins
    //        e.HttpClient.Response.KeepBody = true;
    //        if (e.HttpClient.Response.HasBody)
    //        {
    //            _ = await e.GetResponseBody();
    //        }

    //        foreach (var plugin in _plugins.Where(p => p.Enabled))
    //        {
    //            _cancellationToken?.ThrowIfCancellationRequested();

    //            try
    //            {
    //                await plugin.BeforeResponseAsync(proxyResponseArgs, _cancellationToken ?? CancellationToken.None);
    //            }
    //            catch (Exception ex)
    //            {
    //                ExceptionHandler(ex);
    //            }
    //        }
    //    }
    //}

    //async Task OnAfterResponseAsync(object sender, SessionEventArgs e)
    //{
    //    // read response headers
    //    if (IsProxiedHost(e.HttpClient.Request.RequestUri.Host))
    //    {
    //        var proxyResponseArgs = new ProxyResponseArgs(e, new())
    //        {
    //            SessionData = _pluginData[e.GetHashCode()],
    //            GlobalData = _proxyController.ProxyState.GlobalData
    //        };
    //        if (!proxyResponseArgs.HasRequestUrlMatch(_urlsToWatch))
    //        {
    //            // clean up
    //            _ = _pluginData.Remove(e.GetHashCode(), out _);
    //            return;
    //        }

    //        // necessary to repeat to make the response body
    //        // of mocked requests available to plugins
    //        e.HttpClient.Response.KeepBody = true;

    //        using var scope = _logger.BeginScope(e.HttpClient.Request.Method ?? "", e.HttpClient.Request.Url, e.GetHashCode());

    //        var message = $"{e.HttpClient.Request.Method} {e.HttpClient.Request.Url}";
    //        var loggingContext = new LoggingContext(e);
    //        _logger.LogRequest(message, MessageType.InterceptedResponse, loggingContext);

    //        foreach (var plugin in _plugins.Where(p => p.Enabled))
    //        {
    //            _cancellationToken?.ThrowIfCancellationRequested();

    //            try
    //            {
    //                await plugin.AfterResponseAsync(proxyResponseArgs, _cancellationToken ?? CancellationToken.None);
    //            }
    //            catch (Exception ex)
    //            {
    //                ExceptionHandler(ex);
    //            }
    //        }

    //        _logger.LogRequest(message, MessageType.FinishedProcessingRequest, loggingContext);

    //        // clean up
    //        _ = _pluginData.Remove(e.GetHashCode(), out _);
    //    }
    //}
    
    // Unobtanium ResponseHandler
    private async Task<ResponseEventResponse> OnResponseAsync(object sender, ResponseEventArguments e, CancellationToken cancellationToken)
    {
        // Distributed tracing
        using var activity = ActivitySource.StartActivity(nameof(OnResponseAsync), ActivityKind.Consumer, e.RequestActivity?.Context ?? default);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken ?? CancellationToken.None);
        var uri = e.Request.RequestUri!;

        using var scope = _logger.BeginRequestScope(e.Request.Method, uri, e.RequestId);
        var message = $"{e.Request.Method} {e.Response}";
        _logger.LogRequest(message, MessageType.InterceptedResponse, e.Request);
        HttpResponseMessage? response = null;
        var logPlugins = _plugins.Where(p => p.Enabled && p.OnResponseLogAsync is not null);
        if (logPlugins.Any())
        {
            // Call OnResponseLogAsync for all plugins at the same time and wait for all of them to complete
            var logArguments = new Abstractions.Models.ResponseArguments(e.Request, e.Response, e.RequestId);
            var logTasks = logPlugins
                .Select(plugin => plugin.OnResponseLogAsync!(logArguments, cts.Token))
                .ToArray();
            try
            {
                await Task.WhenAll(logTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in a plugin while logging response {ResponseStatusCode} for request {RequestMethod} {RequestUrl}",
                    e.Response.StatusCode, e.Request.Method, uri);
            }
        }

        foreach (var plugin in _plugins.Where(p => p.Enabled && p.OnResponseAsync is not null))
        {
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                var result = await plugin.OnResponseAsync!(new Abstractions.Models.ResponseArguments(e.Request, response ?? e.Response, e.RequestId), cts.Token);
                if (result is not null)
                {
                    if (result.Request is not null)
                    {
                        // If the plugin modified the request, it is a mistake. Faulty behavior.
                        _logger.LogError("Plugin {PluginName} tried changing the request", plugin.Name);
                    }
                    if (result.Response is not null)
                    {
                        response = result.Response;
                        // Maybe exit the loop here?
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in plugin {PluginName} while processing response {ResponseStatusCode} for request {RequestMethod} {RequestUrl}",
                    plugin.Name, e.Response.StatusCode, e.Request.Method, uri);
            }
        }
        _logger.LogRequest(message, MessageType.FinishedProcessingRequest, e.Request);
        _ = _pluginData.Remove(e.RequestId, out _);
        return response is not null
            ? ResponseEventResponse.ModifyResponse(response)
            : ResponseEventResponse.ContinueResponse();
    }

    //// Allows overriding default certificate validation logic
    //Task OnCertificateValidationAsync(object sender, CertificateValidationEventArgs e)
    //{
    //    // set IsValid to true/false based on Certificate Errors
    //    if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
    //    {
    //        e.IsValid = true;
    //    }

    //    return Task.CompletedTask;
    //}

    //// Allows overriding default client certificate selection logic during mutual authentication
    //Task OnCertificateSelectionAsync(object sender, CertificateSelectionEventArgs e) =>
    //    // set e.clientCertificate to override
    //    Task.CompletedTask;

    private static void PrintHotkeys()
    {
        Console.WriteLine("");
        Console.WriteLine("Hotkeys: issue (w)eb request, (r)ecord, (s)top recording, (c)lear screen");
        Console.WriteLine("Press CTRL+C to stop Dev Proxy");
        Console.WriteLine("");
    }

    private static void ToggleSystemProxy(ToggleSystemProxyAction toggle, string? ipAddress = null, int? port = null)
    {
        var bashScriptPath = Path.Join(ProxyUtils.AppFolder, "toggle-proxy.sh");
        var args = toggle switch
        {
            ToggleSystemProxyAction.On => $"on {ipAddress} {port}",
            ToggleSystemProxyAction.Off => "off",
            _ => throw new NotImplementedException()
        };

        var startInfo = new ProcessStartInfo()
        {
            FileName = "/bin/bash",
            Arguments = $"{bashScriptPath} {args}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process() { StartInfo = startInfo };
        _ = process.Start();
        process.WaitForExit();
    }

    private static int GetProcessId(ClientDetails e)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "lsof",
            Arguments = $"-i :{e.Port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = new Process
        {
            StartInfo = psi
        };
        _ = proc.Start();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        var lines = output.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        var matchingLine = lines.FirstOrDefault(l => l.Contains($"{e.Port}->", StringComparison.OrdinalIgnoreCase));
        if (matchingLine is null)
        {
            return -1;
        }
        var pidString = Regex.Matches(matchingLine, @"^.*?\s+(\d+)")?.FirstOrDefault()?.Groups[1]?.Value;
        if (pidString is null)
        {
            return -1;
        }

        return int.TryParse(pidString, out var pid) ? pid : -1;
    }

    private static void AddProxyHeader(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Via", $"dev-proxy/{ProxyUtils.ProductVersion}");

    public override void Dispose()
    {
        base.Dispose();

        _inactivityTimer?.Dispose();
    }
}
