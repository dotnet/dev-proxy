// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy;

namespace Microsoft.DevProxy;

public class ProxyState : IProxyState
{
    private static readonly object consoleLock = new object();

    public bool IsRecording { get; private set; } = false;
    public List<RequestLog> RequestLogs { get; } = [];
    public Dictionary<string, object> GlobalData { get; } = new() {
        { ProxyUtils.ReportsKey, new Dictionary<string, object>() }
    };
    public IProxyConfiguration ProxyConfiguration { get; }

    private readonly ILogger _logger;
    private readonly IPluginEvents _pluginEvents;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private ExceptionHandler ExceptionHandler => ex => _logger.LogError(ex, "An error occurred in a plugin");

    public ProxyState(ILogger logger, IPluginEvents pluginEvents, IHostApplicationLifetime hostApplicationLifetime, IProxyConfiguration proxyConfiguration)
    {
        _logger = logger;
        _pluginEvents = pluginEvents;
        _hostApplicationLifetime = hostApplicationLifetime;
        ProxyConfiguration = proxyConfiguration;
    }

    public void StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        IsRecording = true;
        PrintRecordingIndicator(IsRecording);
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        PrintRecordingIndicator(IsRecording);

        // clone the list so that we can clear the original
        // list in case a new recording is started, and
        // we let plugins handle previously recorded requests
        var clonedLogs = RequestLogs.ToArray();
        RequestLogs.Clear();
        await _pluginEvents.RaiseRecordingStoppedAsync(new RecordingArgs(clonedLogs)
        {
            GlobalData = GlobalData
        }, ExceptionHandler);
        _logger.LogInformation("DONE");
    }

    public async Task RaiseMockRequestAsync()
    {
        await _pluginEvents.RaiseMockRequestAsync(new EventArgs(), ExceptionHandler);
    }

    public void StopProxy()
    {
        _hostApplicationLifetime.StopApplication();
    }

    private void PrintRecordingIndicator(bool isRecording)
    {
        lock (consoleLock)
        {
            if (isRecording)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.Write("◉");
                Console.ResetColor();
                Console.Error.WriteLine(" Recording... ");
            }
            else
            {
                Console.Error.WriteLine("○ Stopped recording");
            }
        }
    }
}
