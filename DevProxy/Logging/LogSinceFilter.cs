// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Logging;

/// <summary>
/// Filters Dev Proxy log lines by date and time. Most log lines don't carry
/// a timestamp, so each line's time is reconstructed from the log start time
/// and the timestamps that the log does contain: request blocks'
/// <c>time</c> lines (full ISO date), time-of-day prefixes on debug/trace
/// messages and the <c>timestamp</c> property of JSON log lines. A line
/// without a timestamp is assigned the most recent preceding timestamp.
/// </summary>
internal static partial class LogSinceFilter
{
    private static readonly TimeSpan _outOfOrderTolerance = TimeSpan.FromMinutes(1);

    public static IEnumerable<string> Filter(IReadOnlyList<string> lines, DateTime logStartTime, DateTime sinceTime)
    {
        var lineTimes = GetLineTimes(lines, logStartTime);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lineTimes[i] >= sinceTime)
            {
                yield return lines[i];
            }
        }
    }

    internal static DateTime[] GetLineTimes(IReadOnlyList<string> lines, DateTime logStartTime)
    {
        var times = new DateTime[lines.Count];
        var current = logStartTime;

        var i = 0;
        while (i < lines.Count)
        {
            if (RequestStartRegex().IsMatch(lines[i]))
            {
                // Request blocks are written at once and their timestamp is on
                // the 'time' line inside the block, so apply it to the whole block
                var blockEnd = FindRequestBlockEnd(lines, i);
                for (var j = i; j <= blockEnd; j++)
                {
                    if (TryGetTimestamp(lines[j], current, out var blockTime))
                    {
                        current = blockTime;
                        break;
                    }
                }

                // The empty line preceding a request block belongs to it
                if (i > 0 && string.IsNullOrWhiteSpace(lines[i - 1]))
                {
                    times[i - 1] = current;
                }

                for (var j = i; j <= blockEnd; j++)
                {
                    times[j] = current;
                }

                i = blockEnd + 1;
                continue;
            }

            if (TryGetTimestamp(lines[i], current, out var lineTime))
            {
                current = lineTime;
            }

            times[i] = current;
            i++;
        }

        return times;
    }

    private static int FindRequestBlockEnd(IReadOnlyList<string> lines, int start)
    {
        if (RequestEndRegex().IsMatch(lines[start]))
        {
            return start;
        }

        for (var i = start + 1; i < lines.Count; i++)
        {
            if (RequestStartRegex().IsMatch(lines[i]))
            {
                return i - 1;
            }

            if (RequestEndRegex().IsMatch(lines[i]))
            {
                return i;
            }
        }

        return lines.Count - 1;
    }

    private static bool TryGetTimestamp(string line, DateTime current, out DateTime timestamp)
    {
        timestamp = default;

        var match = FullTimestampRegex().Match(line);
        if (match.Success &&
            DateTimeOffset.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset))
        {
            timestamp = dateTimeOffset.LocalDateTime;
            return true;
        }

        match = TimeOfDayRegex().Match(line);
        if (match.Success &&
            TimeSpan.TryParseExact(match.Groups["time"].Value, ["hh\\:mm\\:ss\\.fffffff", "hh\\:mm\\:ss\\.fff"], CultureInfo.InvariantCulture, out var timeOfDay))
        {
            timestamp = ResolveTimeOfDay(timeOfDay, current);
            return true;
        }

        return TryGetJsonTimestamp(line, out timestamp);
    }

    private static DateTime ResolveTimeOfDay(TimeSpan timeOfDay, DateTime current)
    {
        var candidate = current.Date.Add(timeOfDay);

        // Log lines are written in order, so a time of day earlier than the
        // previous timestamp means the log crossed midnight. Allow for small
        // differences caused by concurrent writes.
        if (candidate < current - _outOfOrderTolerance)
        {
            candidate = candidate.AddDays(1);
        }
        else if (candidate - current > TimeSpan.FromDays(1) - _outOfOrderTolerance)
        {
            candidate = candidate.AddDays(-1);
        }

        return candidate;
    }

    private static bool TryGetJsonTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;

        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("timestamp", out var property) &&
                property.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset))
            {
                timestamp = dateTimeOffset.LocalDateTime;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    // Optional ANSI escape sequences around labels, in case colored output was captured
    private const string Ansi = @"(?:\x1b\[[0-9;]*m)*";

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*req\s*" + Ansi + @"\s*\u256d")]
    private static partial Regex RequestStartRegex();

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*\S+\s*" + Ansi + @"\s*\u2570")]
    private static partial Regex RequestEndRegex();

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*time\s*" + Ansi + @"\s*[\u2502\u2570]\s*(?<timestamp>\d{4}-\d{2}-\d{2}T\S+)\s*$")]
    private static partial Regex FullTimestampRegex();

    [GeneratedRegex(@"^(?:\s*" + Ansi + @"\s*(?:dbug|trce)\s*" + Ansi + @"\s*)?\[(?<time>\d{2}:\d{2}:\d{2}\.(?:\d{7}|\d{3}))\]")]
    private static partial Regex TimeOfDayRegex();
}
