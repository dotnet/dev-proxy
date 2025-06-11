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

public sealed class WebSocketServer(int port, ILogger logger) : IDisposable
{
    private readonly ILogger _logger = logger;
    private readonly int _port = port;
    static readonly SemaphoreSlim _webSocketSemaphore = new(1, 1);

    private HttpListener? _listener;
    private WebSocket? _webSocket;

    public bool IsConnected => _webSocket is not null;

#pragma warning disable CA1003
    public event Action<string>? MessageReceived;
#pragma warning restore CA1003

    public async Task StartAsync()
    {
        _listener = new();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();

        while (true)
        {
            var context = await _listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                _webSocket = webSocketContext.WebSocket;
                _ = HandleMessagesAsync(_webSocket);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    public async Task SendAsync<TMsg>(TMsg message)
    {
        if (_webSocket is null)
        {
            return;
        }

        var messageString = JsonSerializer.Serialize(message, ProxyUtils.JsonSerializerOptions);

        // we need a semaphore to avoid multiple simultaneous writes
        // which aren't allowed
        await _webSocketSemaphore.WaitAsync();

        var messageBytes = Encoding.UTF8.GetBytes(messageString);
        await _webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

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
