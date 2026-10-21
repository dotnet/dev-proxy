// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Logging;

using Xunit;

namespace DevProxy.Tests;

public class LogSinceFilterTests
{
    private static readonly DateTime _start = new(2026, 10, 19, 22, 0, 0, DateTimeKind.Local);

    private static string TimeLine(DateTime localTime) =>
        $" time  \u2502 {new DateTimeOffset(localTime).ToUniversalTime():O}";

    private static string[] RequestBlock(string url, DateTime localTime) =>
    [
        "",
        $" req   \u256d GET {url}",
        TimeLine(localTime),
        " skip  \u2502 RetryAfterPlugin: Request not throttled",
        " pass  \u2570 Passed through",
    ];

    [Fact]
    public void Filter_RequestsOnPreviousDays_ExcludedBySinceDuration()
    {
        string[] lines =
        [
            " info    Dev Proxy listening on 127.0.0.1:8000",
            .. RequestBlock("https://example.com/day1", _start.AddMinutes(30)),
            .. RequestBlock("https://example.com/day2", _start.AddDays(1).AddHours(1)),
            .. RequestBlock("https://example.com/day3", _start.AddDays(2).AddHours(1)),
        ];

        var result = LogSinceFilter.Filter(lines, _start, _start.AddDays(2)).ToList();

        Assert.DoesNotContain(result, l => l.Contains("listening", StringComparison.Ordinal));
        Assert.DoesNotContain(result, l => l.Contains("day1", StringComparison.Ordinal));
        Assert.DoesNotContain(result, l => l.Contains("day2", StringComparison.Ordinal));
        Assert.Equal(RequestBlock("https://example.com/day3", _start.AddDays(2).AddHours(1)), result);
    }

    [Fact]
    public void Filter_SinceInFuture_ReturnsNoLines()
    {
        string[] lines =
        [
            " info    Dev Proxy listening on 127.0.0.1:8000",
            .. RequestBlock("https://example.com", _start.AddMinutes(5)),
        ];

        Assert.Empty(LogSinceFilter.Filter(lines, _start, _start.AddYears(1)));
    }

    [Fact]
    public void Filter_SinceBeforeLogStart_ReturnsAllLines()
    {
        string[] lines =
        [
            " info    Dev Proxy listening on 127.0.0.1:8000",
            .. RequestBlock("https://example.com", _start.AddMinutes(5)),
        ];

        Assert.Equal(lines, LogSinceFilter.Filter(lines, _start, _start.AddDays(-1)));
    }

    [Fact]
    public void GetLineTimes_LinesWithoutTimestamp_UseLogStartOrPrecedingTimestamp()
    {
        string[] lines =
        [
            " info    Dev Proxy listening on 127.0.0.1:8000",
            .. RequestBlock("https://example.com", _start.AddHours(1)),
            " info    Configuration changed. Restarting...",
        ];

        var times = LogSinceFilter.GetLineTimes(lines, _start);

        Assert.Equal(_start, times[0]);
        Assert.All(times[1..], t => Assert.Equal(_start.AddHours(1), t));
    }

    [Fact]
    public void GetLineTimes_DebugTimeOfDayAcrossMidnight_RollsOverToNextDay()
    {
        string[] lines =
        [
            " dbug    [23:59:59.0000000] ProxyEngine: before midnight",
            " dbug    [00:00:01.0000000] ProxyEngine: after midnight",
            " dbug    [00:00:00.9990000] ProxyEngine: written slightly out of order",
            " dbug    [23:00:00.0000000] ProxyEngine: next evening",
        ];

        var times = LogSinceFilter.GetLineTimes(lines, _start);

        Assert.Equal(_start.Date.AddHours(23).AddMinutes(59).AddSeconds(59), times[0]);
        Assert.Equal(_start.Date.AddDays(1).AddSeconds(1), times[1]);
        Assert.Equal(_start.Date.AddDays(1).AddMilliseconds(999), times[2]);
        Assert.Equal(_start.Date.AddDays(1).AddHours(23), times[3]);
    }

    [Fact]
    public void GetLineTimes_RequestTimestampJustBeforeMidnight_KeepsPreviousDay()
    {
        var afterMidnight = _start.Date.AddDays(1).AddMilliseconds(1);
        string[] lines =
        [
            .. RequestBlock("https://example.com", afterMidnight),
            " trce    [23:59:59.9990000] ProxyEngine: written slightly out of order",
        ];

        var times = LogSinceFilter.GetLineTimes(lines, _start);

        Assert.Equal(_start.Date.AddDays(1).AddMilliseconds(-1), times[^1]);
    }

    [Fact]
    public void GetLineTimes_JsonLines_UseTimestampProperty()
    {
        var timestamp = _start.AddDays(3);
        string[] lines =
        [
            $$"""{"type":"log","level":"info","message":"hello","timestamp":"{{new DateTimeOffset(timestamp).ToUniversalTime():O}}"}""",
            "{not json",
        ];

        var times = LogSinceFilter.GetLineTimes(lines, _start);

        Assert.Equal(timestamp, times[0]);
        Assert.Equal(timestamp, times[1]);
    }

    [Fact]
    public void FilterLines_NoSince_ReturnsAllLines()
    {
        string[] lines = ["a", "b"];

        Assert.Equal(lines, Commands.LogsCommand.FilterLines(lines, null, _start));
    }
}
