// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Logging;
using DevProxy.State;
using System.CommandLine;
using System.Globalization;

namespace DevProxy.Commands;

internal sealed class LogsCommand : Command
{
    private readonly Option<bool> _followOption = new("--follow", "-f")
    {
        Description = "Follow log output (tail -f style)"
    };

    private readonly Option<int> _linesOption = new("--lines", "-n")
    {
        Description = "Number of lines to show from the end of the log",
        HelpName = "N",
        DefaultValueFactory = _ => 50
    };

    private readonly Option<string?> _sinceOption = new("--since")
    {
        Description = "Show logs since timestamp (e.g., '2026-01-24T14:00:00' or '5m' for 5 minutes ago)",
        HelpName = "time"
    };

    private readonly Option<int?> _pidOption = new("--pid")
    {
        Description = "Show logs from a specific Dev Proxy instance by PID"
    };

    public LogsCommand() : base("logs", "Show logs from running Dev Proxy instance")
    {
        Add(_followOption);
        Add(_linesOption);
        Add(_sinceOption);
        Add(_pidOption);

        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var follow = parseResult.GetValue(_followOption);
        var lines = parseResult.GetValue(_linesOption);
        var since = parseResult.GetValue(_sinceOption);
        var pid = parseResult.GetValue(_pidOption);

        ProxyInstanceState? state;
        if (pid is not null)
        {
            state = await StateManager.LoadStateByPidAsync(pid.Value, cancellationToken);
            if (state is null)
            {
                Console.WriteLine($"No running Dev Proxy instance with PID {pid.Value}.");
                return 1;
            }
        }
        else
        {
            var states = await StateManager.LoadAllStatesAsync(cancellationToken);
            state = StateManager.SelectPrimaryState(states);
            if (state is null)
            {
                Console.WriteLine("Dev Proxy is not running.");
                return 1;
            }

            var notice = GetMultipleInstancesNotice(state, states);
            if (notice is not null)
            {
                await Console.Error.WriteLineAsync(notice);
            }
        }

        var logFile = state.LogFile;

        if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile))
        {
            Console.WriteLine($"Log file not found: {logFile}");
            return 1;
        }

        var sinceTime = LogSinceFilter.ParseSinceOption(since);
        var logStartTime = GetLogStartTime(state);

        if (follow)
        {
            await FollowLogFileAsync(logFile, lines, sinceTime, logStartTime, cancellationToken);
        }
        else
        {
            await ShowLastLinesAsync(logFile, lines, sinceTime, logStartTime);
        }

        return 0;
    }

    internal static DateTimeOffset GetLogStartTime(ProxyInstanceState state)
    {
        using var process = System.Diagnostics.Process.GetProcessById(state.Pid);
        return process.StartTime;
    }

    internal static string? GetMultipleInstancesNotice(ProxyInstanceState selected, IReadOnlyCollection<ProxyInstanceState> states)
    {
        var otherPids = states
            .Where(s => s.Pid != selected.Pid)
            .Select(s => s.Pid.ToString(CultureInfo.InvariantCulture))
            .ToList();
        if (otherPids.Count == 0)
        {
            return null;
        }

        var selectedDescription = selected.AsSystemProxy ? "system proxy instance" : "most recently started instance";
        return $"Multiple Dev Proxy instances are running. Showing logs from PID {selected.Pid} ({selectedDescription}). " +
            $"Other instances: {string.Join(", ", otherPids)}. Use --pid to show logs from a specific instance.";
    }

    private static async Task ShowLastLinesAsync(string logFile, int lineCount, DateTimeOffset? sinceTime, DateTimeOffset logStartTime)
    {
        var lines = new List<string>();
        await foreach (var line in File.ReadLinesAsync(logFile))
        {
            lines.Add(line);
        }

        var filteredLines = FilterLines(lines, sinceTime, logStartTime);
        var linesToShow = filteredLines.TakeLast(lineCount);

        foreach (var line in linesToShow)
        {
            Console.WriteLine(line);
        }
    }

    private static async Task FollowLogFileAsync(string logFile, int initialLines, DateTimeOffset? sinceTime, DateTimeOffset logStartTime, CancellationToken cancellationToken)
    {
        using var reader = new LogFileReader(logFile, logStartTime, sinceTime);
        foreach (var line in (await reader.ReadLinesAsync(cancellationToken)).TakeLast(initialLines))
        {
            Console.WriteLine(line);
        }

        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(logFile)!, Path.GetFileName(logFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        var newLineEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Changed += (_, _) =>
        {
            _ = newLineEvent.TrySetResult(true);
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for file change or cancellation
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(1000); // Check every second even without file changes

                try
                {
                    _ = await newLineEvent.Task.WaitAsync(cts.Token);
                    newLineEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout - check for new content anyway
                }

                foreach (var line in await reader.ReadLinesAsync(cancellationToken))
                {
                    Console.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // File might be temporarily locked
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    internal static IEnumerable<string> FilterLines(IReadOnlyList<string> lines, DateTimeOffset? sinceTime, DateTimeOffset logStartTime)
    {
        if (sinceTime == null)
        {
            return lines;
        }

        return LogSinceFilter.Filter(lines, logStartTime, sinceTime.Value);
    }
}
