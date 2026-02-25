// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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

    public LogsCommand() : base("logs", "Show logs from running Dev Proxy instance")
    {
        Add(_followOption);
        Add(_linesOption);
        Add(_sinceOption);

        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var follow = parseResult.GetValue(_followOption);
        var lines = parseResult.GetValue(_linesOption);
        var since = parseResult.GetValue(_sinceOption);

        var state = await StateManager.LoadStateAsync(cancellationToken);

        if (state == null)
        {
            Console.WriteLine("Dev Proxy is not running.");
            return 1;
        }

        var logFile = state.LogFile;

        if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile))
        {
            Console.WriteLine($"Log file not found: {logFile}");
            return 1;
        }

        var sinceTime = ParseSinceOption(since);

        if (follow)
        {
            await FollowLogFileAsync(logFile, lines, sinceTime, cancellationToken);
        }
        else
        {
            await ShowLastLinesAsync(logFile, lines, sinceTime);
        }

        return 0;
    }

    private static DateTime? ParseSinceOption(string? since)
    {
        if (string.IsNullOrEmpty(since))
        {
            return null;
        }

        // Try parsing as a duration (e.g., "5m", "1h", "30s")
        if (since.Length >= 2)
        {
            var unit = since[^1];
            if (int.TryParse(since[..^1], out var value))
            {
                return unit switch
                {
                    's' => DateTime.Now.AddSeconds(-value),
                    'm' => DateTime.Now.AddMinutes(-value),
                    'h' => DateTime.Now.AddHours(-value),
                    'd' => DateTime.Now.AddDays(-value),
                    _ => null
                };
            }
        }

        // Try parsing as a datetime
        if (DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
        {
            return dateTime;
        }

        return null;
    }

    private static async Task ShowLastLinesAsync(string logFile, int lineCount, DateTime? sinceTime)
    {
        var lines = new List<string>();
        await foreach (var line in File.ReadLinesAsync(logFile))
        {
            lines.Add(line);
        }

        var filteredLines = FilterLines(lines, sinceTime);
        var linesToShow = filteredLines.TakeLast(lineCount);

        foreach (var line in linesToShow)
        {
            Console.WriteLine(line);
        }
    }

    private static async Task FollowLogFileAsync(string logFile, int initialLines, DateTime? sinceTime, CancellationToken cancellationToken)
    {
        // Show initial lines
        await ShowLastLinesAsync(logFile, initialLines, sinceTime);

        // Follow new lines using FileSystemWatcher and reading from last position
        var lastPosition = new FileInfo(logFile).Length;

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

                // Read new content
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > lastPosition)
                {
                    _ = fs.Seek(lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        if (sinceTime == null || LineMatchesSince(line, sinceTime.Value))
                        {
                            Console.WriteLine(line);
                        }
                    }

                    lastPosition = fs.Length;
                }
            }
            catch (IOException)
            {
                // File might be temporarily locked
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static IEnumerable<string> FilterLines(IEnumerable<string> lines, DateTime? sinceTime)
    {
        if (sinceTime == null)
        {
            return lines;
        }

        return lines.Where(line => LineMatchesSince(line, sinceTime.Value));
    }

    private static bool LineMatchesSince(string line, DateTime sinceTime)
    {
        // Parse timestamp from line format: [HH:mm:ss.fff] ...
        if (line.Length < 14 || line[0] != '[' || line[13] != ']')
        {
            return true; // Include lines that don't match the expected format
        }

        var timestampStr = line[1..13]; // "HH:mm:ss.fff"
        if (TimeSpan.TryParseExact(timestampStr, "hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture, out var timeOfDay))
        {
            // Assume the log is from today
            var lineTime = DateTime.Today.Add(timeOfDay);

            // If line time is in the future, assume it's from yesterday
            if (lineTime > DateTime.Now)
            {
                lineTime = lineTime.AddDays(-1);
            }

            return lineTime >= sinceTime;
        }

        return true; // Include lines with unparseable timestamps
    }
}