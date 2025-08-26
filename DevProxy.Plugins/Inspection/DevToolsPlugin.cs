// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Inspection.CDP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Inspection;

public enum PreferredBrowser
{
    Edge,
    Chrome,
    EdgeDev,
    EdgeBeta
}

public sealed class DevToolsPluginConfiguration
{
    public PreferredBrowser PreferredBrowser { get; set; } = PreferredBrowser.Edge;

    /// <summary>
    /// Path to the browser executable. If not set, the plugin will try to find
    /// the browser executable based on the PreferredBrowser.
    /// </summary>
    /// <remarks>Use this value when you install the browser in a non-standard
    /// path.</remarks>
    public string PreferredBrowserPath { get; set; } = string.Empty;
}

public sealed class DevToolsPlugin(
    HttpClient httpClient,
    ILogger<DevToolsPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<DevToolsPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection), IDisposable
{
    private readonly Dictionary<string, GetResponseBodyResultParams> _responseBody = [];

    private CancellationToken? _cancellationToken;
    private WebSocketServer? _webSocket;

    public override string Name => nameof(DevToolsPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        await base.InitializeAsync(e, cancellationToken);

        InitInspector();
    }

    public override Func<RequestArguments, CancellationToken, Task>? ProvideRequestGuidanceAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(ProvideRequestGuidanceAsync));

        if (_webSocket?.IsConnected != true)
        {
            return;
        }

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return;
        }

        var requestId = args.RequestId!;
        var headers = args.Request.Headers
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

        // Add content headers if they exist
        if (args.Request.Content?.Headers != null)
        {
            foreach (var header in args.Request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        var postData = args.Request.Content != null ? await args.Request.Content.ReadAsStringAsync(cancellationToken) : null;

        var requestWillBeSentMessage = new RequestWillBeSentMessage
        {
            Params = new()
            {
                RequestId = requestId,
                LoaderId = "1",
                DocumentUrl = args.Request.RequestUri?.ToString() ?? string.Empty,
                Request = new()
                {
                    Url = args.Request.RequestUri?.ToString() ?? string.Empty,
                    Method = args.Request.Method.Method,
                    Headers = headers,
                    PostData = postData
                },
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                WallTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Initiator = new()
                {
                    Type = "other"
                }
            }
        };
        await _webSocket.SendAsync(requestWillBeSentMessage, cancellationToken);

        // must be included to avoid the "Provisional headers are shown" warning
        var requestWillBeSentExtraInfoMessage = new RequestWillBeSentExtraInfoMessage
        {
            Params = new()
            {
                RequestId = requestId,
                // must be included in the message or the message will be rejected
                AssociatedCookies = [],
                Headers = headers
            }
        };
        await _webSocket.SendAsync(requestWillBeSentExtraInfoMessage, cancellationToken);
    };

    public override Func<ResponseArguments, CancellationToken, Task>? ProvideResponseGuidanceAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(ProvideResponseGuidanceAsync));

        if (_webSocket?.IsConnected != true)
        {
            return;
        }

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
            return;
        }

        var body = new GetResponseBodyResultParams
        {
            Body = string.Empty,
            Base64Encoded = false
        };

        if (args.Response.Content != null)
        {
            var contentType = args.Response.Content.Headers.ContentType?.MediaType;
            if (IsTextResponse(contentType))
            {
                body.Body = await args.Response.Content.ReadAsStringAsync(cancellationToken);
                body.Base64Encoded = false;
            }
            else
            {
                var bytes = await args.Response.Content.ReadAsByteArrayAsync(cancellationToken);
                body.Body = Convert.ToBase64String(bytes);
                body.Base64Encoded = true;
            }
        }

        _responseBody.Add(args.RequestId, body);

        var requestId = args.RequestId!;

        var responseHeaders = args.Response.Headers
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

        // Add content headers if they exist
        if (args.Response.Content?.Headers != null)
        {
            foreach (var header in args.Response.Content.Headers)
            {
                responseHeaders[header.Key] = string.Join(", ", header.Value);
            }
        }

        var responseReceivedMessage = new ResponseReceivedMessage
        {
            Params = new()
            {
                RequestId = requestId,
                LoaderId = "1",
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                Type = "XHR",
                Response = new()
                {
                    Url = args.Request.RequestUri?.ToString() ?? string.Empty,
                    Status = (int)args.Response.StatusCode,
                    StatusText = args.Response.ReasonPhrase ?? string.Empty,
                    Headers = responseHeaders,
                    MimeType = args.Response.Content?.Headers.ContentType?.MediaType ?? string.Empty
                },
                HasExtraInfo = true
            }
        };

        await _webSocket.SendAsync(responseReceivedMessage, cancellationToken);

        if (args.Response.Content?.Headers.ContentType?.MediaType == "text/event-stream")
        {
            await SendBodyAsDataReceivedAsync(requestId, body.Body, cancellationToken);
        }

        var loadingFinishedMessage = new LoadingFinishedMessage
        {
            Params = new()
            {
                RequestId = requestId,
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                EncodedDataLength = args.Response.Content != null ? (await args.Response.Content.ReadAsByteArrayAsync(cancellationToken)).Length : 0
            }
        };
        await _webSocket.SendAsync(loadingFinishedMessage, cancellationToken);
    };

    public override async Task AfterRequestLogAsync(RequestLogArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRequestLogAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (_webSocket?.IsConnected != true ||
            e.RequestLog.MessageType == MessageType.InterceptedRequest ||
            e.RequestLog.MessageType == MessageType.InterceptedResponse)
        {
            return;
        }

        var message = new EntryAddedMessage
        {
            Params = new()
            {
                Entry = new()
                {
                    Source = "network",
                    Text = string.Join(" ", e.RequestLog.Message),
                    Level = Entry.GetLevel(e.RequestLog.MessageType),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Url = e.RequestLog.Request?.RequestUri?.ToString(),
                    NetworkRequestId = e.RequestLog.RequestId!
                }
            }
        };
        await _webSocket.SendAsync(message, cancellationToken);

        Logger.LogTrace("Left {Name}", nameof(AfterRequestLogAsync));
    }

    private string GetBrowserPath()
    {
        if (!string.IsNullOrEmpty(Configuration.PreferredBrowserPath))
        {
            Logger.LogInformation("{PreferredBrowserPath} was set to {Path}. Ignoring {PreferredBrowser} setting.", nameof(Configuration.PreferredBrowserPath), Configuration.PreferredBrowserPath, nameof(Configuration.PreferredBrowser));
            return Configuration.PreferredBrowserPath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                PreferredBrowser.Edge => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                PreferredBrowser.EdgeDev => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge Dev\Application\msedge.exe"),
                PreferredBrowser.EdgeBeta => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge Beta\Application\msedge.exe"),
                _ => throw new NotSupportedException($"{Configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                PreferredBrowser.Edge => "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                PreferredBrowser.EdgeDev => "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Dev",
                PreferredBrowser.EdgeBeta => "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Beta",
                _ => throw new NotSupportedException($"{Configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => "/opt/google/chrome/chrome",
                PreferredBrowser.Edge => "/opt/microsoft/msedge/msedge",
                PreferredBrowser.EdgeDev => "/opt/microsoft/msedge-dev/msedge",
                PreferredBrowser.EdgeBeta => "/opt/microsoft/msedge-beta/msedge",
                _ => throw new NotSupportedException($"{Configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else
        {
            throw new NotSupportedException("Unsupported operating system.");
        }
    }

    private void InitInspector()
    {
        var browserPath = string.Empty;

        try
        {
            browserPath = GetBrowserPath();
        }
        catch (NotSupportedException ex)
        {
            Logger.LogError(ex, "Error starting {Plugin}. Error finding the browser.", Name);
            return;
        }

        // check if the browser is installed
        if (!File.Exists(browserPath))
        {
            Logger.LogError("Error starting {Plugin}. Browser executable not found at {BrowserPath}", Name, browserPath);
            return;
        }

        var port = GetFreePort();
        _webSocket = new(port, Logger);
        _webSocket.MessageReceived += SocketMessageReceived;
        _ = _webSocket.StartAsync();

        var inspectionUrl = $"http://localhost:9222/devtools/inspector.html?ws=localhost:{port}";
        var profilePath = Path.Combine(Path.GetTempPath(), "devtools-devproxy");
        var args = $"{inspectionUrl} --remote-debugging-port=9222 --user-data-dir=\"{profilePath}\"";

        Logger.LogInformation("{Name} available at {InspectionUrl}", Name, inspectionUrl);

        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = browserPath,
                Arguments = args,
                // suppress default output
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        _ = process.Start();
    }

    private void SocketMessageReceived(string msg)
    {
        if (_webSocket is null)
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize<Message>(msg, ProxyUtils.JsonSerializerOptions);
            switch (message?.Method)
            {
                case "Network.getResponseBody":
                    var getResponseBodyMessage = JsonSerializer.Deserialize<GetResponseBodyMessage>(msg, ProxyUtils.JsonSerializerOptions);
                    if (getResponseBodyMessage is null)
                    {
                        return;
                    }
                    _ = HandleNetworkGetResponseBodyAsync(getResponseBodyMessage, _cancellationToken ?? CancellationToken.None);
                    break;
                case "Network.streamResourceContent":
                    _ = HandleNetworkStreamResourceContentAsync(message, _cancellationToken ?? CancellationToken.None);
                    break;
                default:
                    break;
            }
        }
        catch { }
    }

    private async Task HandleNetworkStreamResourceContentAsync(Message message, CancellationToken cancellationToken)
    {
        if (_webSocket is null || message.Id is null)
        {
            return;
        }

        var result = new StreamResourceContentResult
        {
            Id = (int)message.Id,
            Result = new()
            {
                BufferedData = string.Empty
            }
        };

        await _webSocket.SendAsync(result, cancellationToken);
    }

    private async Task HandleNetworkGetResponseBodyAsync(GetResponseBodyMessage message, CancellationToken cancellationToken)
    {
        if (_webSocket is null || message.Params?.RequestId is null)
        {
            return;
        }

        if (!_responseBody.TryGetValue(message.Params.RequestId, out var value) ||
            // should never happen because the message is sent from devtools
            // and Id is required on all socket messages but theoretically
            // it is possible
            message.Id is null)
        {
            return;
        }

        var result = new GetResponseBodyResult
        {
            Id = (int)message.Id,
            Result = new()
            {
                Body = value.Body,
                Base64Encoded = value.Base64Encoded
            }
        };

        await _webSocket.SendAsync(result, cancellationToken);
    }

    private async Task SendBodyAsDataReceivedAsync(string requestId, string? body, CancellationToken cancellationToken)
    {
        if (_webSocket is null || string.IsNullOrEmpty(body))
        {
            return;
        }

        var base64Encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var dataReceivedMessage = new DataReceivedMessage
        {
            Params = new()
            {
                RequestId = requestId,
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                Data = base64Encoded,
                DataLength = body.Length,
                EncodedDataLength = base64Encoded.Length
            }
        };

        await _webSocket.SendAsync(dataReceivedMessage, cancellationToken);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsTextResponse(string? contentType)
    {
        var isTextResponse = false;

        if (contentType is not null &&
            (contentType.IndexOf("text", StringComparison.OrdinalIgnoreCase) > -1 ||
            contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) > -1))
        {
            isTextResponse = true;
        }

        return isTextResponse;
    }

    public void Dispose()
    {
        _webSocket?.Dispose();
    }
}
