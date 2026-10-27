// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Logging;

/// <summary>
/// Filters Dev Proxy log lines by timestamp instant. Detached regular messages
/// carry full timestamps; legacy lines are reconstructed from the log start time
/// and the timestamps that the log contains: request blocks'
/// <c>time</c> lines (full ISO date), time-of-day prefixes on debug/trace
/// messages and the <c>timestamp</c> property of JSON log lines. A line
/// without a timestamp is assigned the most recent preceding timestamp.
/// </summary>
internal sealed partial class LogSinceFilter(DateTimeOffset logStartTime, DateTimeOffset? sinceTime)
{
    private static readonly TimeSpan _outOfOrderTolerance = TimeSpan.FromMinutes(1);
    private readonly List<string> _pendingLines = [];
    private DateTimeOffset _current = logStartTime;
    private bool _inRequestBlock;

    internal IReadOnlyList<string> AppendLine(string line)
    {
        if (sinceTime is null)
        {
            return [line];
        }

        List<string> result = [];
        var startsRequest = RequestStartRegex().IsMatch(line);
        if (_inRequestBlock && startsRequest)
        {
            result.AddRange(ProcessPendingLines());
            _inRequestBlock = false;
        }

        _pendingLines.Add(line);
        _inRequestBlock |= startsRequest;
        if (_inRequestBlock)
        {
            if (RequestEndRegex().IsMatch(line))
            {
                result.AddRange(ProcessPendingLines());
                _inRequestBlock = false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            // Keep blank lines until the next entry identifies their timestamp.
            result.AddRange(ProcessPendingLines());
        }

        return result;
    }

    private List<string> ProcessPendingLines()
    {
        var times = GetLineTimes(_pendingLines, _current);
        List<string> result = [];
        for (var i = 0; i < _pendingLines.Count; i++)
        {
            if (times[i] >= sinceTime)
            {
                result.Add(_pendingLines[i]);
            }
        }

        _current = times[^1];
        _pendingLines.Clear();
        return result;
    }

    public static IEnumerable<string> Filter(IReadOnlyList<string> lines, DateTimeOffset logStartTime, DateTimeOffset sinceTime)
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

    internal static DateTimeOffset[] GetLineTimes(IReadOnlyList<string> lines, DateTimeOffset logStartTime)
    {
        var times = new DateTimeOffset[lines.Count];
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

    private static bool TryGetTimestamp(string line, DateTimeOffset current, out DateTimeOffset timestamp)
    {
        timestamp = default;

        var match = FullTimestampRegex().Match(line);
        if (!match.Success)
        {
            match = TimestampPrefixRegex().Match(line);
        }
        if (match.Success &&
            DateTimeOffset.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset))
        {
            timestamp = dateTimeOffset;
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

    internal static DateTimeOffset ResolveTimeOfDay(TimeSpan timeOfDay, DateTimeOffset current, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var currentWallTime = TimeZoneInfo.ConvertTime(current, timeZone).DateTime;
        var candidate = currentWallTime.Date.Add(timeOfDay);

        // Log lines are written in order, so a time of day earlier than the
        // previous timestamp normally means midnight. During the repeated DST
        // hour, however, the clock can move backwards on the same date.
        if (candidate < currentWallTime - _outOfOrderTolerance)
        {
            if (!timeZone.IsAmbiguousTime(currentWallTime) || !timeZone.IsAmbiguousTime(candidate))
            {
                candidate = candidate.AddDays(1);
            }
        }
        else if (candidate - currentWallTime > TimeSpan.FromDays(1) - _outOfOrderTolerance)
        {
            candidate = candidate.AddDays(-1);
        }

        if (timeZone.IsAmbiguousTime(candidate))
        {
            var instants = timeZone.GetAmbiguousTimeOffsets(candidate)
                .Select(offset => new DateTimeOffset(candidate, offset))
                .Order()
                .ToArray();
            return instants.FirstOrDefault(instant => instant >= current - _outOfOrderTolerance, instants[^1]);
        }

        return new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));
    }

    private static bool TryGetJsonTimestamp(string line, out DateTimeOffset timestamp)
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
                timestamp = dateTimeOffset;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    internal static DateTimeOffset? ParseSinceOption(string? since)
    {
        if (string.IsNullOrEmpty(since))
        {
            return null;
        }

        if (since.Length >= 2 && int.TryParse(since[..^1], out var value))
        {
            var now = DateTimeOffset.UtcNow;
            return since[^1] switch
            {
                's' => now.AddSeconds(-value),
                'm' => now.AddMinutes(-value),
                'h' => now.AddHours(-value),
                'd' => now.AddDays(-value),
                _ => null
            };
        }

        return DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp
            : null;
    }

    // Optional ANSI escape sequences around labels, in case colored output was captured
    private const string Ansi = @"(?:\x1b\[[0-9;]*m)*";

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*req\s*" + Ansi + @"\s*\u256d")]
    private static partial Regex RequestStartRegex();

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*\S*\s*" + Ansi + @"\s*\u2570")]
    private static partial Regex RequestEndRegex();

    [GeneratedRegex(@"^\s*" + Ansi + @"\s*time\s*" + Ansi + @"\s*[\u2502\u2570]\s*(?<timestamp>\d{4}-\d{2}-\d{2}T\S+)\s*$")]
    private static partial Regex FullTimestampRegex();

    [GeneratedRegex(@"^(?:\s*" + Ansi + @"\s*(?:trce|dbug|info|warn|fail|crit)\s*" + Ansi + @"\s*(?:[\u2502\u2570]\s*)?)?\[(?<timestamp>\d{4}-\d{2}-\d{2}T[^\]]+)\]")]
    private static partial Regex TimestampPrefixRegex();

    [GeneratedRegex(@"^(?:\s*" + Ansi + @"\s*(?:dbug|trce)\s*" + Ansi + @"\s*)?\[(?<time>\d{2}:\d{2}:\d{2}\.(?:\d{7}|\d{3}))\]")]
    private static partial Regex TimeOfDayRegex();
}
