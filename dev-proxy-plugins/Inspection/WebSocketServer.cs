// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.DevProxy.Plugins.Inspection;

using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Logging;

public class WebSocketServer(int port, ILogger logger)
{
    private HttpListener? listener;
    private readonly int _port = port;
    private readonly ILogger _logger = logger;
    private WebSocket? webSocket;
    static readonly SemaphoreSlim webSocketSemaphore = new(1, 1);

    public bool IsConnected => webSocket is not null;
    public event Action<string>? MessageReceived;

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

    public async Task StartAsync()
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/");
        listener.Start();

        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                webSocket = webSocketContext.WebSocket;
                _ = HandleMessagesAsync(webSocket);
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
        if (webSocket is null)
        {
            return;
        }

        var messageString = JsonSerializer.Serialize(message, ProxyUtils.JsonSerializerOptions);

        // we need a semaphore to avoid multiple simultaneous writes
        // which aren't allowed
        await webSocketSemaphore.WaitAsync();

        byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);
        await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

        webSocketSemaphore.Release();
    }
}
