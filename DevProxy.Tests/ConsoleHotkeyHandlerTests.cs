// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Xunit;

namespace DevProxy.Tests;

/// <summary>
/// Verifies the interactive hotkey dispatch and banners restored after the
/// Kestrel cut-over (the legacy engine's ReadKeysAsync/PrintHotkeys behavior).
/// </summary>
public sealed class ConsoleHotkeyHandlerTests
{
    private static (ConsoleHotkeyHandler handler, FakeProxyStateController controller, RecordingConsole console)
        CreateHandler(OutputFormat output = OutputFormat.Text)
    {
        var controller = new FakeProxyStateController();
        var console = new RecordingConsole();
        var configuration = new FakeProxyConfiguration { Output = output };
        var handler = new ConsoleHotkeyHandler(controller, configuration, console);
        return (handler, controller, console);
    }

    [Fact]
    public async Task HandleKeyAsync_R_StartsRecording()
    {
        var (handler, controller, _) = CreateHandler();

        await handler.HandleKeyAsync(ConsoleKey.R, CancellationToken.None);

        Assert.Equal(1, controller.StartRecordingCalls);
        Assert.Equal(0, controller.StopRecordingCalls);
        Assert.Equal(0, controller.MockRequestCalls);
    }

    [Fact]
    public async Task HandleKeyAsync_S_StopsRecording()
    {
        var (handler, controller, _) = CreateHandler();

        await handler.HandleKeyAsync(ConsoleKey.S, CancellationToken.None);

        Assert.Equal(1, controller.StopRecordingCalls);
        Assert.Equal(0, controller.StartRecordingCalls);
    }

    [Fact]
    public async Task HandleKeyAsync_W_IssuesMockRequest()
    {
        var (handler, controller, _) = CreateHandler();

        await handler.HandleKeyAsync(ConsoleKey.W, CancellationToken.None);

        Assert.Equal(1, controller.MockRequestCalls);
    }

    [Fact]
    public async Task HandleKeyAsync_C_ClearsAndReprintsHotkeys_InTextMode()
    {
        var (handler, controller, console) = CreateHandler(OutputFormat.Text);

        await handler.HandleKeyAsync(ConsoleKey.C, CancellationToken.None);

        Assert.Equal(1, console.ClearCount);
        Assert.Contains(console.Lines, l => l.Contains("Hotkeys:", StringComparison.Ordinal));
        // C is purely a display action — no recording/mock side effects.
        Assert.Equal(0, controller.StartRecordingCalls);
        Assert.Equal(0, controller.MockRequestCalls);
    }

    [Fact]
    public async Task HandleKeyAsync_C_ClearsAndReprintsApiInstructions_InJsonMode()
    {
        var (handler, _, console) = CreateHandler(OutputFormat.Json);

        await handler.HandleKeyAsync(ConsoleKey.C, CancellationToken.None);

        Assert.Equal(1, console.ClearCount);
        Assert.Contains(console.Lines, l => l.Contains("/proxy/mockRequest", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, l => l.Contains("Hotkeys:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleKeyAsync_UnknownKey_DoesNothing()
    {
        var (handler, controller, console) = CreateHandler();

        await handler.HandleKeyAsync(ConsoleKey.X, CancellationToken.None);

        Assert.Equal(0, controller.StartRecordingCalls);
        Assert.Equal(0, controller.StopRecordingCalls);
        Assert.Equal(0, controller.MockRequestCalls);
        Assert.Equal(0, console.ClearCount);
        Assert.Empty(console.Lines);
    }

    [Fact]
    public void PrintHotkeys_WritesHotkeyHints()
    {
        var (handler, _, console) = CreateHandler();

        handler.PrintHotkeys();

        Assert.Contains(console.Lines, l => l.Contains("(w)eb request", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("(r)ecord", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("(s)top recording", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("(c)lear screen", StringComparison.Ordinal));
        Assert.Contains(console.Lines, l => l.Contains("CTRL+C", StringComparison.Ordinal));
    }

    [Fact]
    public void PrintApiInstructions_WritesAllApiCommands()
    {
        var (handler, _, console) = CreateHandler(OutputFormat.Json);

        handler.PrintApiInstructions();

        var joined = string.Join('\n', console.Lines);
        Assert.Contains("/proxy/mockRequest", joined, StringComparison.Ordinal);
        Assert.Contains("\\\"recording\\\": true", joined, StringComparison.Ordinal);
        Assert.Contains("\\\"recording\\\": false", joined, StringComparison.Ordinal);
        Assert.Contains("/proxy/stopProxy", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintBanner_TextMode_PrintsHotkeys()
    {
        var (handler, _, console) = CreateHandler(OutputFormat.Text);

        handler.PrintBanner();

        Assert.Contains(console.Lines, l => l.Contains("Hotkeys:", StringComparison.Ordinal));
    }

    [Fact]
    public void PrintBanner_JsonMode_PrintsApiInstructions()
    {
        var (handler, _, console) = CreateHandler(OutputFormat.Json);

        handler.PrintBanner();

        Assert.Contains(console.Lines, l => l.Contains("/proxy/mockRequest", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, l => l.Contains("Hotkeys:", StringComparison.Ordinal));
    }
}
