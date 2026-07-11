// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Proxy;
using DevProxy.State;
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
        var reconciliation = await SystemProxyManager.ReconcileOrphanedSystemProxiesAsync(cancellationToken);

        var states = await StateManager.LoadAllStatesAsync(cancellationToken);
        if (states.Count == 0 && reconciliation.Orphans.Count == 0)
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

        ReportReconciledOrphans(reconciliation);

        return exitCode;
    }

    private static async Task<int> StopByPidAsync(int pid, bool force, CancellationToken cancellationToken)
    {
        // Capture a potential orphaned system-proxy record before LoadStateByPidAsync
        // prunes (deletes) the stale state file for a dead PID.
        var orphan = (await StateManager.GetOrphanedSystemProxyStatesAsync(cancellationToken))
            .Find(o => o.Pid == pid);

        var state = await StateManager.LoadStateByPidAsync(pid, cancellationToken);
        if (state is not null)
        {
            return await StopInstanceAsync(state, force, cancellationToken);
        }

        if (orphan is not null)
        {
            var liveOwner = await StateManager.FindSystemProxyInstanceAsync(cancellationToken);
            if (liveOwner is null)
            {
                SystemProxyManager.Disable();
                Console.WriteLine($"Restored system proxy left by crashed Dev Proxy (PID: {pid}).");
            }
            else
            {
                Console.WriteLine($"Removed stale record for crashed Dev Proxy (PID: {pid}); system proxy is owned by a running instance (PID: {liveOwner.Pid}).");
            }

            await StateManager.DeleteStateAsync(pid, cancellationToken);
            return 0;
        }

        Console.WriteLine($"No running Dev Proxy instance with PID {pid}.");
        return 1;
    }

    private static void ReportReconciledOrphans(SystemProxyManager.OrphanReconciliation reconciliation)
    {
        foreach (var orphan in reconciliation.Orphans)
        {
            Console.WriteLine(reconciliation.SystemProxyDisabled
                ? $"Restored system proxy left by crashed Dev Proxy (PID: {orphan.Pid})."
                : $"Removed stale record for crashed Dev Proxy (PID: {orphan.Pid}); system proxy is owned by a running instance.");
        }
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
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await httpClient.PostAsync($"{state.ApiUrl}/proxy/stopProxy", null, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                Console.WriteLine($"Stopping Dev Proxy (PID: {state.Pid})...");

                // Wait for process to exit
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        var process = Process.GetProcessById(state.Pid);
                        if (process.HasExited)
                        {
                            break;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process has exited
                        break;
                    }

                    await Task.Delay(200, cancellationToken);
                }

                // Verify the process actually exited before cleaning up state
                var exited = false;
                try
                {
                    var proc = Process.GetProcessById(state.Pid);
                    exited = proc.HasExited;
                }
                catch (ArgumentException)
                {
                    exited = true;
                }

                if (!exited)
                {
                    Console.WriteLine($"Dev Proxy (PID: {state.Pid}) did not stop in time.");
                    Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
                    return 1;
                }

                await StateManager.DeleteStateAsync(state.Pid, cancellationToken);
                Console.WriteLine($"Dev Proxy (PID: {state.Pid}) stopped.");
                return 0;
            }

            Console.WriteLine($"Failed to stop Dev Proxy (PID: {state.Pid}): {response.StatusCode}");
            Console.WriteLine($"Use --force --pid {state.Pid} to forcefully terminate the process.");
            return 1;
        }
        catch (HttpRequestException ex)
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
        // A killed process can't run its own cleanup, so restore the system proxy
        // on its behalf before terminating it.
        if (state.AsSystemProxy)
        {
            SystemProxyManager.Disable();
        }

        try
        {
            var process = Process.GetProcessById(state.Pid);
            process.Kill();

            Console.WriteLine($"Forcefully terminated Dev Proxy (PID: {state.Pid}).");
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

        await StateManager.DeleteStateAsync(state.Pid, cancellationToken);
        return 0;
    }
}