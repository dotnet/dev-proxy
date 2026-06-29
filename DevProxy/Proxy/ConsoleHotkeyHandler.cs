// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using DevProxy.Abstractions.Proxy;

namespace DevProxy.Proxy;

/// <summary>
/// Renders the startup banner and maps interactive key presses to proxy actions.
/// This is the testable core of the interactive console — it has no console-loop
/// or host-lifetime concerns, so it can be exercised with a fake
/// <see cref="ISystemConsole"/> and a fake <see cref="IProxyStateController"/>.
///
/// <para>Key → action dispatch:</para>
/// <code>
///   key
///    ├─ R ──► controller.StartRecording()
///    ├─ S ──► controller.StopRecordingAsync()
///    ├─ W ──► controller.MockRequestAsync()      (issue a (w)eb request)
///    ├─ C ──► console.Clear() + reprint banner   (Json → API help, else hotkeys)
///    └─ *  ──► (ignored)
/// </code>
/// </summary>
internal sealed class ConsoleHotkeyHandler(
    IProxyStateController controller,
    IProxyConfiguration configuration,
    ISystemConsole console)
{
    /// <summary>
    /// Prints the banner appropriate for the current output mode: machine-readable
    /// API instructions in JSON mode, human hotkey hints otherwise.
    /// </summary>
    public void PrintBanner()
    {
        if (configuration.Output == OutputFormat.Json)
        {
            PrintApiInstructions();
        }
        else
        {
            PrintHotkeys();
        }
    }

    public void PrintHotkeys()
    {
        console.WriteLine("");
        console.WriteLine("Hotkeys: issue (w)eb request, (r)ecord, (s)top recording, (c)lear screen");
        console.WriteLine("Press CTRL+C to stop Dev Proxy");
        console.WriteLine("");
    }

    public void PrintApiInstructions()
    {
        var baseUrl = $"http://{configuration.IPAddress}:{configuration.ApiPort}/proxy";
        var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        console.WriteLine("");
        console.WriteLine($"{{\"type\":\"log\",\"level\":\"info\",\"message\":\"Issue web request: curl -X POST {baseUrl}/mockRequest\",\"category\":\"ProxyEngine\",\"timestamp\":\"{timestamp}\"}}");
        console.WriteLine($"{{\"type\":\"log\",\"level\":\"info\",\"message\":\"Start recording: curl -X POST {baseUrl} -H \\\"Content-Type: application/json\\\" -d '{{\\\"recording\\\": true}}'\",\"category\":\"ProxyEngine\",\"timestamp\":\"{timestamp}\"}}");
        console.WriteLine($"{{\"type\":\"log\",\"level\":\"info\",\"message\":\"Stop recording: curl -X POST {baseUrl} -H \\\"Content-Type: application/json\\\" -d '{{\\\"recording\\\": false}}'\",\"category\":\"ProxyEngine\",\"timestamp\":\"{timestamp}\"}}");
        console.WriteLine($"{{\"type\":\"log\",\"level\":\"info\",\"message\":\"Stop Dev Proxy: curl -X POST {baseUrl}/stopProxy\",\"category\":\"ProxyEngine\",\"timestamp\":\"{timestamp}\"}}");
        console.WriteLine("");
    }

    /// <summary>
    /// Dispatches a single key press. Unknown keys are ignored.
    /// </summary>
    public async Task HandleKeyAsync(ConsoleKey key, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case ConsoleKey.R:
                controller.StartRecording();
                break;
            case ConsoleKey.S:
                await controller.StopRecordingAsync(cancellationToken);
                break;
            case ConsoleKey.W:
                await controller.MockRequestAsync(cancellationToken);
                break;
            case ConsoleKey.C:
                console.Clear();
                PrintBanner();
                break;
            default:
                break;
        }
    }
}
