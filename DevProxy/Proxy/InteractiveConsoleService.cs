// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Commands;

namespace DevProxy.Proxy;

/// <summary>
/// Host-side interactive console: prints the startup banner, honors the
/// <c>--record</c> flag, and (in an interactive terminal) listens for hotkeys
/// to drive recording and mock requests.
///
/// <para>This lives in the host — not the Kestrel engine — on purpose. The engine
/// is intentionally headless (it depends only on the HTTP/proxy abstractions and
/// must not reach into host concerns like <see cref="IProxyStateController"/>).
/// The legacy Titanium engine owned this loop only because it happened to live in
/// the host assembly; the canonical home is here.</para>
///
/// <para>Startup sequence (after the host has fully started, so the banner appears
/// below the engine's "listening on ..." log):</para>
/// <code>
///   ApplicationStarted
///        │
///        ├─ --record set?           ──► controller.StartRecording()
///        │
///        ├─ Output == Json?         ──► print API instructions (even non-interactive,
///        │                              so agents can drive the HTTP API)
///        ├─ else interactive?       ──► print hotkeys
///        │
///        └─ interactive? ── no ──► return (no key loop when piped/daemon/CI)
///                       └─ yes ─► poll KeyAvailable → ReadKey → HandleKeyAsync
/// </code>
/// </summary>
internal sealed class InteractiveConsoleService(
    IProxyStateController controller,
    IProxyConfiguration configuration,
    ISystemConsole console,
    IHostApplicationLifetime lifetime,
    ILogger<InteractiveConsoleService> logger) : BackgroundService
{
    // Matches the legacy engine's poll cadence; small enough to feel instant,
    // large enough to keep the idle CPU cost negligible.
    private static readonly TimeSpan _keyPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly ConsoleHotkeyHandler _handler = new(controller, configuration, console);

    /// <summary>
    /// Hotkeys are only usable from a real terminal driven by a human. Skip the
    /// loop when stdin is redirected (piped/tests), when running as the internal
    /// detached daemon, or under CI — reading keys there would either throw or
    /// busy-spin against an input that never produces a human key press.
    /// </summary>
    private bool IsInteractive =>
        !console.IsInputRedirected &&
        !DevProxyCommand.IsInternalDaemon &&
        Environment.GetEnvironmentVariable("CI") is null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForApplicationStartedAsync(stoppingToken))
        {
            return;
        }

        if (configuration.Record)
        {
            controller.StartRecording();
        }

        if (configuration.Output == OutputFormat.Json)
        {
            // Always print API instructions in machine mode so LLMs/agents can use
            // the HTTP API even when there's no interactive terminal.
            _handler.PrintApiInstructions();
        }
        else if (IsInteractive)
        {
            _handler.PrintHotkeys();
        }

        if (!IsInteractive)
        {
            return;
        }

        logger.LogDebug("Interactive hotkeys enabled.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (!console.KeyAvailable)
                {
                    await Task.Delay(_keyPollInterval, stoppingToken);
                }

                await _handler.HandleKeyAsync(console.ReadKey(), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Waits for the host to finish starting so the banner is printed after the
    /// engine's startup logs (and the bound port is known). Returns <c>false</c>
    /// if shutdown was requested before the host started.
    /// </summary>
    private async Task<bool> WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => startedTcs.TrySetResult());
        try
        {
            await startedTcs.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
