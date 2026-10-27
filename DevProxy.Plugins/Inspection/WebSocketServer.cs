// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

public sealed class WebSocketServer(string host, int port, string token, ILogger logger) : IDisposable
{
    // Origins used by the Chromium DevTools frontend: the bundled frontend
    // (devtools://devtools/...) and the frontend served by the browser's
    // remote debugging server, which the plugin launches on port 9222.
    // Browsers always send Origin on WebSocket handshakes and don't apply
    // CORS to them, so checking it prevents arbitrary web pages from
    // connecting and reading intercepted traffic.
    internal static readonly string[] AllowedOrigins =
    [
        "devtools://devtools",
        "http://localhost:9222",
        "http://127.0.0.1:9222"
    ];

    private readonly ILogger _logger = logger;
    private readonly string _host = host;
    private readonly int _port = port;
    private readonly string _path = $"/{token}";
    static readonly SemaphoreSlim _webSocketSemaphore = new(1, 1);

    private HttpListener? _listener;
    private WebSocket? _webSocket;

    public bool IsConnected => _webSocket is not null;

#pragma warning disable CA1003
    public event Action<string>? MessageReceived;
    public event Action? ClientConnected;
#pragma warning restore CA1003

    /// <summary>
    /// Starts listening for connections. Throws if the listener can't be
    /// started, eg. because the port is already in use.
    /// </summary>
    public void Start()
    {
        _listener = new();
        _listener.Prefixes.Add($"http://{_host}:{_port}/");
        _listener.Start();

        _ = AcceptConnectionsAsync(_listener);
    }

    internal static bool IsAllowedOrigin(string? origin) =>
        origin is not null &&
        AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    // Origin is client-controlled and the listener may be reachable from the
    // network, so escape control characters to prevent log forging.
    internal static string SanitizeForLog(string? value) =>
        value is null
            ? "(none)"
            : string.Concat(value.Select(c => char.IsControl(c)
                ? $"\\u{(int)c:x4}"
                : c.ToString()));

    private async Task AcceptConnectionsAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // listener has been stopped
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            if (!string.Equals(context.Request.Url?.AbsolutePath, _path, StringComparison.Ordinal))
            {
                _logger.LogWarning("[WS] Rejected DevTools connection with an invalid token");
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }

            var origin = context.Request.Headers["Origin"];
            if (!IsAllowedOrigin(origin))
            {
                _logger.LogWarning("[WS] Rejected DevTools connection from origin {Origin}", SanitizeForLog(origin));
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }

            try
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                _webSocket = webSocketContext.WebSocket;
                ClientConnected?.Invoke();
                _ = HandleMessagesAsync(_webSocket);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "[WS] Error accepting DevTools connection");
            }
        }
    }

    public async Task SendAsync<TMsg>(TMsg message, CancellationToken cancellationToken)
        where TMsg : notnull
    {
        if (_webSocket is null)
        {
            return;
        }

        var messageString = JsonSerializer.Serialize(message, ProxyUtils.JsonSerializerOptions);

        // we need a semaphore to avoid multiple simultaneous writes
        // which aren't allowed
        await _webSocketSemaphore.WaitAsync(cancellationToken);

        var messageBytes = Encoding.UTF8.GetBytes(messageString);
        await _webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);

        _ = _webSocketSemaphore.Release();
    }

    private async Task HandleMessagesAsync(WebSocket ws)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(new byte[8192]);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer.Array!, 0, result.Count);
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (InvalidOperationException)
        {
            _logger.LogError("[WS] Tried to receive message while already reading one.");
        }
    }

    public void Dispose()
    {
        _listener?.Close();
    }
}
