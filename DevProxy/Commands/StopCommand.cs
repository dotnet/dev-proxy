// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Proxy;
using DevProxy.State;
using Microsoft.Extensions.Logging.Abstractions;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;

namespace DevProxy.Commands;

internal sealed class StopCommand : Command
{
    private readonly ILogger<StopCommand> _logger;
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Force stop the proxy by killing the process"
    };

    private readonly Option<int?> _pidOption = new("--pid")
    {
        Description = "Stop a specific Dev Proxy instance by PID"
    };

    public StopCommand(ILogger<StopCommand> logger) : base("stop", """
                Stop running Dev Proxy instances.

                JSON output: one result event per outcome, with pid, status, message and,
                when applicable, hint. Status is one of: notRunning, stopped, stopTimeout,
                failed, forceStopped, alreadyStopped, refused, systemProxyRestored,
                staleRecordRemoved.
                """)
    {
        _logger = logger;
        Add(_forceOption);
        Add(_pidOption);
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var force = parseResult.GetValue(_forceOption);
        var pid = parseResult.GetValue(_pidOption);
        var reporter = new StopReporter(_logger,
            parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text);

        if (pid is not null)
        {
            return await StopByPidAsync(pid.Value, force, reporter, cancellationToken);
        }

        // Reconcile any system-proxy registrations left behind by crashed
        // instances first — this must run before LoadAllStatesAsync, which prunes
        // (deletes) the stale state files it depends on.
        var reconciliation = await SystemProxyManager.ReconcileOrphanedSystemProxiesAsync(cancellationToken);

        var states = await StateManager.LoadAllStatesAsync(cancellationToken);
        if (states.Count == 0 && reconciliation.Orphans.Count == 0)
        {
            reporter.Report(null, "notRunning", "Dev Proxy is not running.");
            return 1;
        }

        var exitCode = 0;
        foreach (var state in states)
        {
            var result = await StopInstanceAsync(state, force, reporter, cancellationToken);
            if (result != 0)
            {
                exitCode = result;
            }
        }

        ReportReconciledOrphans(reconciliation, reporter);

        return exitCode;
    }

    private static async Task<int> StopByPidAsync(int pid, bool force, StopReporter reporter, CancellationToken cancellationToken)
    {
        // Capture a potential orphaned system-proxy record before LoadStateByPidAsync
        // prunes (deletes) the stale state file for a dead PID.
        var orphan = (await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
            .Find(o => o.Pid == pid);

        var state = await StateManager.LoadStateByPidAsync(pid, cancellationToken);
        if (state is not null)
        {
            return await StopInstanceAsync(state, force, reporter, cancellationToken);
        }

        if (orphan is not null)
        {
            var liveOwner = await StateManager.FindSystemProxyInstanceAsync(cancellationToken);
            if (liveOwner is null)
            {
                new SystemProxyManager(NullLogger<SystemProxyManager>.Instance).Disable();
                reporter.Report(pid, "systemProxyRestored", $"Restored system proxy left by crashed Dev Proxy (PID: {pid}).");
            }
            else
            {
                reporter.Report(pid, "staleRecordRemoved", $"Removed stale record for crashed Dev Proxy (PID: {pid}); system proxy is owned by a running instance (PID: {liveOwner.Pid}).");
            }

            await StateManager.DeleteStateAsync(orphan, cancellationToken);
            return 0;
        }

        reporter.Report(pid, "notRunning", $"No running Dev Proxy instance with PID {pid}.");
        return 1;
    }

    private static void ReportReconciledOrphans(SystemProxyManager.OrphanReconciliation reconciliation, StopReporter reporter)
    {
        foreach (var orphan in reconciliation.Orphans)
        {
            if (reconciliation.SystemProxyDisabled)
            {
                reporter.Report(orphan.Pid, "systemProxyRestored", $"Restored system proxy left by crashed Dev Proxy (PID: {orphan.Pid}).");
            }
            else
            {
                reporter.Report(orphan.Pid, "staleRecordRemoved", $"Removed stale record for crashed Dev Proxy (PID: {orphan.Pid}); system proxy is owned by a running instance.");
            }
        }
    }

    private static async Task<int> StopInstanceAsync(ProxyInstanceState state, bool force, StopReporter reporter, CancellationToken cancellationToken)
    {
        if (force)
        {
            return await ForceStopAsync(state, reporter, cancellationToken);
        }

        var forceHint = $"Use --force --pid {state.Pid} to forcefully terminate the process.";

        // Try graceful shutdown via API
        try
        {
            using var httpClient = await ApiSecurity.CreateClientAsync(state, TimeSpan.FromSeconds(10), cancellationToken);
            using var response = await httpClient.PostAsync("/proxy/stopProxy", null, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                reporter.Progress($"Stopping Dev Proxy (PID: {state.Pid})...");

                // Wait for process to exit
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) &&
                    StateManager.IsInstanceAlive(state))
                {
                    await Task.Delay(200, cancellationToken);
                }

                // Verify the process actually exited before cleaning up state.
                // A PID reused by an unrelated process counts as exited.
                if (StateManager.IsInstanceAlive(state))
                {
                    reporter.Report(state.Pid, "stopTimeout", $"Dev Proxy (PID: {state.Pid}) did not stop in time.", forceHint);
                    return 1;
                }

                await StateManager.DeleteStateAsync(state, cancellationToken);
                reporter.Report(state.Pid, "stopped", $"Dev Proxy (PID: {state.Pid}) stopped.");
                return 0;
            }

            reporter.Report(state.Pid, "failed", $"Failed to stop Dev Proxy (PID: {state.Pid}): {response.StatusCode}", forceHint);
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            reporter.Report(state.Pid, "failed", $"Failed to connect to Dev Proxy API (PID: {state.Pid}): {ex.Message}", forceHint);
            return 1;
        }
        catch (TaskCanceledException)
        {
            reporter.Report(state.Pid, "failed", $"Timeout waiting for Dev Proxy (PID: {state.Pid}) to respond.", forceHint);
            return 1;
        }
    }

    private static async Task<int> ForceStopAsync(ProxyInstanceState state, StopReporter reporter, CancellationToken cancellationToken)
    {
        var identity = GetProcessIdentity(state);
        if (identity == ProcessIdentity.Unverifiable)
        {
            reporter.Report(state.Pid, "refused", $"Cannot verify that PID {state.Pid} is Dev Proxy; refusing to terminate it.");
            return 1;
        }

        // null: process gone; Mismatch: PID reused by an unrelated process.
        var isAlive = identity is ProcessIdentity.Match or ProcessIdentity.NotRecorded;

        if (state.AsSystemProxy)
        {
            // A killed process can't run its own cleanup, so restore the system
            // proxy on its behalf before terminating it. If the instance is
            // already gone, treat it as an orphan and leave the system proxy
            // alone when another live instance owns it.
            if (isAlive || await StateManager.FindSystemProxyInstanceAsync(cancellationToken) is null)
            {
                new SystemProxyManager(NullLogger<SystemProxyManager>.Instance).Disable();
            }
        }

        if (!isAlive)
        {
            reporter.Report(state.Pid, "alreadyStopped", "Dev Proxy process not found (already stopped).");
            await StateManager.DeleteStateAsync(state, cancellationToken);
            return 0;
        }

        try
        {
            using var process = Process.GetProcessById(state.Pid);
            // Re-verify on the handle we're about to kill.
            identity = StateManager.VerifyProcessIdentity(process, state);
            if (identity == ProcessIdentity.Unverifiable)
            {
                reporter.Report(state.Pid, "refused", $"Cannot verify that PID {state.Pid} is Dev Proxy; refusing to terminate it.");
                return 1;
            }

            if (identity == ProcessIdentity.Mismatch)
            {
                reporter.Report(state.Pid, "alreadyStopped", "Dev Proxy process not found (already stopped).");
            }
            else
            {
                process.Kill();
                // Let the process exit so its PID-keyed token can be removed.
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(exitTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Token is cleaned up later by the stale-credential sweep
                }
                reporter.Report(state.Pid, "forceStopped", $"Forcefully terminated Dev Proxy (PID: {state.Pid}).");
            }
        }
        catch (ArgumentException)
        {
            reporter.Report(state.Pid, "alreadyStopped", "Dev Proxy process not found (already stopped).");
        }
        catch (InvalidOperationException)
        {
            reporter.Report(state.Pid, "alreadyStopped", "Dev Proxy process has already exited.");
        }
        catch (Exception ex)
        {
            reporter.Report(state.Pid, "failed", $"Failed to kill process: {ex.Message}");
            return 1;
        }

        await StateManager.DeleteStateAsync(state, cancellationToken);
        return 0;
    }

    private static ProcessIdentity? GetProcessIdentity(ProxyInstanceState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            return StateManager.VerifyProcessIdentity(process, state);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class StopReporter(ILogger logger, OutputFormat outputFormat)
    {
        public void Progress(string message)
        {
            if (outputFormat == OutputFormat.Text)
            {
                Console.WriteLine(message);
            }
        }

        public void Report(int? pid, string status, string message, string? hint = null)
        {
            if (outputFormat == OutputFormat.Json)
            {
                logger.LogStructuredOutput(JsonSerializer.Serialize(new { pid, status, message, hint }, ProxyUtils.JsonSerializerOptions));
                return;
            }

            Console.WriteLine(message);
            if (hint is not null)
            {
                Console.WriteLine(hint);
            }
        }
    }
}