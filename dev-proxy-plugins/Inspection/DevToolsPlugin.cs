// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.Inspection.CDP;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Inspection;

public enum PreferredBrowser
{
    Edge,
    Chrome,
    EdgeDev,
    EdgeBeta
}

public class DevToolsPluginConfiguration
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

public class DevToolsPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    private WebSocketServer? webSocket;
    private readonly Dictionary<string, GetResponseBodyResultParams> responseBody = [];

    public override string Name => nameof(DevToolsPlugin);
    private readonly DevToolsPluginConfiguration _configuration = new();

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();
        ConfigSection?.Bind(_configuration);

        InitInspector();

        PluginEvents.BeforeRequest += BeforeRequestAsync;
        PluginEvents.AfterResponse += AfterResponseAsync;
        PluginEvents.AfterRequestLog += AfterRequestLogAsync;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string GetBrowserPath(DevToolsPluginConfiguration configuration)
    {
        if (!string.IsNullOrEmpty(configuration.PreferredBrowserPath))
        {
            Logger.LogInformation("{preferredBrowserPath} was set to {path}. Ignoring {preferredBrowser} setting.", nameof(configuration.PreferredBrowserPath), configuration.PreferredBrowserPath, nameof(configuration.PreferredBrowser));
            return configuration.PreferredBrowserPath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                PreferredBrowser.Edge => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                PreferredBrowser.EdgeDev => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge Dev\Application\msedge.exe"),
                PreferredBrowser.EdgeBeta => Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge Beta\Application\msedge.exe"),
                _ => throw new NotSupportedException($"{configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                PreferredBrowser.Edge => "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                PreferredBrowser.EdgeDev => "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Dev",
                PreferredBrowser.EdgeBeta => "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Beta",
                _ => throw new NotSupportedException($"{configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return configuration.PreferredBrowser switch
            {
                PreferredBrowser.Chrome => "/opt/google/chrome/chrome",
                PreferredBrowser.Edge => "/opt/microsoft/msedge/msedge",
                PreferredBrowser.EdgeDev => "/opt/microsoft/msedge-dev/msedge",
                PreferredBrowser.EdgeBeta => "/opt/microsoft/msedge-beta/msedge",
                _ => throw new NotSupportedException($"{configuration.PreferredBrowser} is an unsupported browser. Please change your PreferredBrowser setting for {Name}.")
            };
        }
        else
        {
            throw new NotSupportedException("Unsupported operating system.");
        }
    }

    private Process[] GetBrowserProcesses(string browserPath)
    {
        return Process.GetProcesses().Where(p =>
        {
            try
            {
                return p.MainModule is not null && p.MainModule.FileName == browserPath;
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Error while checking process: {Ex}", ex.Message);
                return false;
            }
        }).ToArray();
    }

    private void InitInspector()
    {
        var browserPath = string.Empty;

        try
        {
            browserPath = GetBrowserPath(_configuration);
        }
        catch (NotSupportedException ex)
        {
            Logger.LogError(ex, "Error starting {plugin}. Error finding the browser.", Name);
            return;
        }

        // check if the browser is installed
        if (!File.Exists(browserPath))
        {
            Logger.LogError("Error starting {plugin}. Browser executable not found at {browserPath}", Name, browserPath);
            return;
        }

        // find if the process is already running
        var processes = GetBrowserProcesses(browserPath);

        if (processes.Any())
        {
            var ids = string.Join(", ", processes.Select(p => p.Id.ToString()));
            Logger.LogError("Found existing browser process {processName} with IDs {processIds}. Could not start {plugin}. Please close existing browser processes and restart Dev Proxy", browserPath, ids, Name);
            return;
        }

        var port = GetFreePort();
        webSocket = new WebSocketServer(port, Logger);
        webSocket.MessageReceived += SocketMessageReceived;
        _ = webSocket.StartAsync();

        var inspectionUrl = $"http://localhost:9222/devtools/inspector.html?ws=localhost:{port}";
        var args = $"{inspectionUrl} --remote-debugging-port=9222 --profile-directory=devproxy";

        Logger.LogInformation("{name} available at {inspectionUrl}", Name, inspectionUrl);

        var process = new Process
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
        process.Start();
    }

    private void SocketMessageReceived(string msg)
    {
        if (webSocket is null)
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize<GetResponseBodyMessage>(msg, ProxyUtils.JsonSerializerOptions);
            if (message?.Method == "Network.getResponseBody")
            {
                var requestId = message.Params?.RequestId;
                if (requestId is null ||
                    !responseBody.TryGetValue(requestId, out GetResponseBodyResultParams? value) ||
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
                _ = webSocket.SendAsync(result);
            }
        }
        catch { }
    }

    private static string GetRequestId(Titanium.Web.Proxy.Http.Request? request)
    {
        if (request is null)
        {
            return string.Empty;
        }

        return request.GetHashCode().ToString();
    }

    private async Task BeforeRequestAsync(object sender, ProxyRequestArgs e)
    {
        if (webSocket?.IsConnected != true)
        {
            return;
        }

        var requestId = GetRequestId(e.Session.HttpClient.Request);
        var headers = e.Session.HttpClient.Request.Headers
            .ToDictionary(h => h.Name, h => h.Value);

        var requestWillBeSentMessage = new RequestWillBeSentMessage
        {
            Params = new()
            {
                RequestId = requestId,
                LoaderId = "1",
                DocumentUrl = e.Session.HttpClient.Request.Url,
                Request = new()
                {
                    Url = e.Session.HttpClient.Request.Url,
                    Method = e.Session.HttpClient.Request.Method,
                    Headers = headers,
                    PostData = e.Session.HttpClient.Request.HasBody ? e.Session.HttpClient.Request.BodyString : null
                },
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                WallTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Initiator = new()
                {
                    Type = "other"
                }
            }
        };
        await webSocket.SendAsync(requestWillBeSentMessage);

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
        await webSocket.SendAsync(requestWillBeSentExtraInfoMessage);
    }

    private async Task AfterResponseAsync(object sender, ProxyResponseArgs e)
    {
        if (webSocket?.IsConnected != true)
        {
            return;
        }

        var body = new GetResponseBodyResultParams
        {
            Body = string.Empty,
            Base64Encoded = false
        };
        if (e.Session.HttpClient.Response.HasBody)
        {
            if (IsTextResponse(e.Session.HttpClient.Response.ContentType))
            {
                body.Body = e.Session.HttpClient.Response.BodyString;
                body.Base64Encoded = false;
            }
            else
            {
                body.Body = Convert.ToBase64String(e.Session.HttpClient.Response.Body);
                body.Base64Encoded = true;
            }
        }
        responseBody.Add(e.Session.HttpClient.Request.GetHashCode().ToString(), body);

        var requestId = GetRequestId(e.Session.HttpClient.Request);

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
                    Url = e.Session.HttpClient.Request.Url,
                    Status = e.Session.HttpClient.Response.StatusCode,
                    StatusText = e.Session.HttpClient.Response.StatusDescription,
                    Headers = e.Session.HttpClient.Response.Headers
                        .ToDictionary(h => h.Name, h => h.Value),
                    MimeType = e.Session.HttpClient.Response.ContentType
                }
            }
        };

        await webSocket.SendAsync(responseReceivedMessage);

        var loadingFinishedMessage = new LoadingFinishedMessage
        {
            Params = new()
            {
                RequestId = requestId,
                Timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000,
                EncodedDataLength = e.Session.HttpClient.Response.HasBody ? e.Session.HttpClient.Response.Body.Length : 0
            }
        };
        await webSocket.SendAsync(loadingFinishedMessage);
    }

    private static bool IsTextResponse(string? contentType)
    {
        var isTextResponse = false;

        if (contentType is not null &&
            (contentType.IndexOf("text") > -1 ||
            contentType.IndexOf("json") > -1))
        {
            isTextResponse = true;
        }

        return isTextResponse;
    }

    private async Task AfterRequestLogAsync(object? sender, RequestLogArgs e)
    {
        if (webSocket?.IsConnected != true ||
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
                    Url = e.RequestLog.Context?.Session.HttpClient.Request.Url,
                    NetworkRequestId = GetRequestId(e.RequestLog.Context?.Session.HttpClient.Request)
                }
            }
        };
        await webSocket.SendAsync(message);
    }
}
