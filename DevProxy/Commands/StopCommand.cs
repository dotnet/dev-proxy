// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using DevProxy.State;
using Microsoft.Extensions.Logging.Abstractions;
using System.CommandLine;
using System.Diagnostics;

namespace DevProxy.Commands;

internal sealed class StopCommand : Command
{
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Force stop the proxy by killing the process"
    };

    private readonly Option<int?> _pidOption = new("--pid")
    {
        Description = "Stop a specific Dev Proxy instance by PID"
    };

    public StopCommand() : base("stop", "Stop running Dev Proxy instances")
    {
        Add(_forceOption);
        Add(_pidOption);
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var force = parseResult.GetValue(_forceOption);
        var pid = parseResult.GetValue(_pidOption);

        if (pid is not null)
        {
            return await StopByPidAsync(pid.Value, force, cancellationToken);
        }

        // Reconcile any system-proxy registrations left behind by crashed
        // instances first — this must run before LoadAllStatesAsync, which prunes
        // (deletes) the stale state files it depends on.
        var reconciliation = await SystemProxyManager.ReconcileOrphanedSystemProxiesAsync(cancellationToken: cancellationToken);

        var states = await StateManager.LoadAllStatesAsync(cancellationToken);
        if (states.Count == 0 && reconciliation.OrphanedPids.Count == 0)
        {
            Console.WriteLine("Dev Proxy is not running.");
            return 1;
        }

        var exitCode = 0;
        foreach (var state in states)
        {
            var result = await StopInstanceAsync(state, force, cancellationToken);
            if (result != 0)
            {
                exitCode = result;
            }
        }

        if (!ReportReconciledOrphans(reconciliation))
        {
            exitCode = 1;
        }

        return exitCode;
    }

    private static async Task<int> StopByPidAsync(int pid, bool force, CancellationToken cancellationToken)
    {
        // Recover an orphan before looking up live state so failed recovery keeps
        // its registration available for a later retry.
        var orphan = (await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
            .Find(o => o.Pid == pid);

        if (orphan is not null)
        {
            var liveOwner = await SystemProxyManager.FindLiveSystemProxyOwnerAsync(cancellationToken);
            if (liveOwner is null)
            {
                if (new SystemProxyManager(NullLogger<SystemProxyManager>.Instance).Restore() is SystemProxyRestoreResult.Failed)
                {
                    // Keep the state record so a later stop or start retries.
                    await Console.Error.WriteLineAsync($"Couldn't restore the system proxy left by crashed Dev Proxy (PID: {pid}). Run 'devproxy system-proxy reset' or see https://go.devproxy.net?go=proxy-reset.");
                    return 1;
                }
                Console.WriteLine($"Restored system proxy left by crashed Dev Proxy (PID: {pid}).");
            }
            else
            {
                Console.WriteLine($"Removed stale record for crashed Dev Proxy (PID: {pid}); system proxy is owned by a running instance (PID: {liveOwner}).");
            }

            await StateManager.DeleteStateAsync(orphan, cancellationToken);
            return 0;
        }

        var state = await StateManager.LoadStateByPidAsync(pid, cancellationToken);
        if (state is not null)
        {
            return await StopInstanceAsync(state, force, cancellationToken);
        }

        Console.WriteLine($"No running Dev Proxy instance with PID {pid}.");
        return 1;
    }

    private static bool ReportReconciledOrphans(SystemProxyManager.OrphanReconciliation reconciliation)
    {
        if (reconciliation.Outcome is SystemProxyManager.OrphanRestoreOutcome.Failed)
        {
            Console.Error.WriteLine($"Couldn't restore the system proxy left by crashed Dev Proxy (PID: {string.Join(", ", reconciliation.OrphanedPids)}). Run 'devproxy system-proxy reset' or see https://go.devproxy.net?go=proxy-reset.");
            return false;
        }

        foreach (var pid in reconciliation.OrphanedPids)
        {
            Console.WriteLine(reconciliation.Outcome is SystemProxyManager.OrphanRestoreOutcome.Restored
                ? $"Restored system proxy left by crashed Dev Proxy (PID: {pid})."
                : $"Removed stale record for crashed Dev Proxy (PID: {pid}); system proxy is owned by a running instance.");
        }
        return true;
    }

    private static async Task<int> StopInstanceAsync(ProxyInstanceState state, bool force, CancellationToken cancellationToken)
    {
        if (force)
        {
            return await ForceStopAsync(state, cancellationToken);
        }

        // Try graceful shutdown via API
        try
        {
            using var httpClient = await ApiSecurity.CreateClientAsync(state, TimeSpan.FromSeconds(10), cancellationToken);
            using var response = await httpClient.PostAsync("/proxy/stopProxy", null, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                Console.WriteLine($"Stopping Dev Proxy (PID: {state.Pid})...");

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
                    Console.WriteLine($"Dev Proxy (PID: {state.Pid}) did not stop in time.");
                    Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
                    return 1;
                }

                await StateManager.DeleteStateAsync(state, cancellationToken);
                Console.WriteLine($"Dev Proxy (PID: {state.Pid}) stopped.");
                return 0;
            }

            Console.WriteLine($"Failed to stop Dev Proxy (PID: {state.Pid}): {response.StatusCode}");
            Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"Failed to connect to Dev Proxy API (PID: {state.Pid}): {ex.Message}");
            Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"Timeout waiting for Dev Proxy (PID: {state.Pid}) to respond.");
            Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
            return 1;
        }
    }

    private static async Task<int> ForceStopAsync(ProxyInstanceState state, CancellationToken cancellationToken)
    {
        var identity = GetProcessIdentity(state);
        if (identity == ProcessIdentity.Unverifiable)
        {
            Console.WriteLine($"Cannot verify that PID {state.Pid} is Dev Proxy; refusing to terminate it.");
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
            Console.WriteLine("Dev Proxy process not found (already stopped).");
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
                Console.WriteLine($"Cannot verify that PID {state.Pid} is Dev Proxy; refusing to terminate it.");
                return 1;
            }

            if (identity == ProcessIdentity.Mismatch)
            {
                Console.WriteLine("Dev Proxy process not found (already stopped).");
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
                Console.WriteLine($"Forcefully terminated Dev Proxy (PID: {state.Pid}).");
            }
        }
        catch (ArgumentException)
        {
            Console.WriteLine("Dev Proxy process not found (already stopped).");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Dev Proxy process has already exited.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to kill process: {ex.Message}");
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
}