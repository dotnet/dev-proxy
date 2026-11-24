// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;

using Xunit;

namespace DevProxy.Tests;

public class LogSinceFilterTests
{
    private static readonly DateTime _start = new(2026, 10, 19, 22, 0, 0, DateTimeKind.Local);

    private static string TimeLine(DateTimeOffset timestamp) =>
        $" time  \u2502 {timestamp:O}";

    private static string[] RequestBlock(string url, DateTimeOffset timestamp) =>
    [
        "",
        $" req   \u256d GET {url}",
        TimeLine(timestamp),
        " skip  \u2502 RetryAfterPlugin: Request not throttled",
        " pass  \u2570 Passed through",
    ];

    private static string[] RequestBlockWithNormalTerminator(DateTimeOffset timestamp)
    {
        var formatter = new ProxyConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions { IncludeScopes = true }));
        var scopeProvider = new LoggerExternalScopeProvider();
        using var scope = scopeProvider.Push(new Dictionary<string, object> { ["requestId"] = 1 });
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        Write("GET https://example.com", MessageType.InterceptedRequest);
        Write(timestamp.ToString("O", CultureInfo.InvariantCulture), MessageType.Timestamp);
        Write("done", MessageType.Normal, "Plugin");
        Write(string.Empty, MessageType.FinishedProcessingRequest);

        return writer.ToString().Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[..^1];

        void Write(string message, MessageType type, string category = ProxyConsoleFormatter.DefaultCategoryName)
        {
            var log = new RequestLog(message, type, context: null);
            var entry = new LogEntry<RequestLog>(LogLevel.Information, category, new EventId(0), log, null, (state, _) => state.Message);
            formatter.Write(entry, scopeProvider, writer);
        }
    }

    [Fact]
    public void Filter_NormalMessageTerminator_DoesNotMisdateFollowingMessages()
    {
        var request = RequestBlockWithNormalTerminator(_start.AddMinutes(5));
        var laterTime = new DateTimeOffset(_start.AddHours(2));
        var laterMessage = $" info    [{laterTime:O}] Later message";
        string[] lines = [.. request, laterMessage, "Continuation of later message"];

        Assert.Equal("       \u2570 Plugin: done", request[^1]);
        Assert.Equal([laterMessage, "Continuation of later message"],
            LogSinceFilter.Filter(lines, _start, _start.AddHours(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLinesAsync_NormalMessageTerminator_EmitsCompletedBlockImmediately(bool ansi)
    {
        var request = RequestBlockWithNormalTerminator(_start.AddMinutes(5));
        if (ansi)
        {
            request[^1] = "\u001b[39;49m      \u001b[0m \u2570 Plugin: done";
        }

        var path = Path.GetTempFileName();
        try
        {
            using var reader = new LogFileReader(path, _start, _start.AddMinutes(1));
            await File.WriteAllTextAsync(path, string.Join('\n', request[..^1]) + "\n");
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, request[^1] + "\n");
            Assert.Equal(request, await reader.ReadLinesAsync(CancellationToken.None));

            var later = $" info    [{new DateTimeOffset(_start.AddHours(2)):O}] Independent message";
            await File.AppendAllTextAsync(path, later + "\n");
            Assert.Equal([later], await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

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

    [Theory]
    [InlineData("America/New_York", 11, 1, 1, 0)]
    [InlineData("Europe/Berlin", 10, 25, 2, 0)]
    [InlineData("Australia/Lord_Howe", 4, 5, 1, 30)]
    public void ResolveTimeOfDay_DaylightSavingRollback_KeepsSameDate(string zoneId, int month, int day, int hour, int minute)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var current = new DateTime(2026, month, day, hour, 59, 59);
        var timeOfDay = new TimeSpan(hour, minute, 1);

        var beforeRollback = new DateTimeOffset(current, timeZone.GetAmbiguousTimeOffsets(current).Max());
        var timestamp = LogSinceFilter.ResolveTimeOfDay(timeOfDay, beforeRollback, timeZone);

        var expected = new DateTimeOffset(current.Date.Add(timeOfDay), timeZone.GetAmbiguousTimeOffsets(current).Min());
        Assert.Equal(expected, timestamp);
        Assert.True(timestamp > beforeRollback);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("json")]
    [InlineData("text")]
    public async Task Filter_DaylightSavingRepeatedHour_UsesInstantsInSnapshotsAndFollowers(string format)
    {
        var beforeRollback = new DateTimeOffset(2026, 11, 1, 1, 50, 0, TimeSpan.FromHours(-4));
        var afterRollback = new DateTimeOffset(2026, 11, 1, 1, 20, 0, TimeSpan.FromHours(-5));
        var cutoff = LogSinceFilter.ParseSinceOption("2026-11-01T01:10:00-05:00")!.Value;
        var start = beforeRollback.AddHours(-1);
        var oldLines = FormatEntry(beforeRollback, "old");
        var newLines = FormatEntry(afterRollback, "new");
        string[] lines = [.. oldLines, .. newLines];

        Assert.Equal(TimeSpan.FromHours(-5), cutoff.Offset);
        Assert.Equal(newLines, LogSinceFilter.Filter(lines, start, cutoff));
        Assert.Equal(newLines, Commands.LogsCommand.FilterLines(lines, cutoff, start));

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, string.Join('\n', oldLines) + "\n");
            using var reader = new LogFileReader(path, start, cutoff);
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, string.Join('\n', newLines) + "\n");
            Assert.Equal(newLines, await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }

        string[] FormatEntry(DateTimeOffset timestamp, string message) => format switch
        {
            "request" => RequestBlock($"https://example.com/{message}", timestamp),
            "json" => [$$"""{"timestamp":"{{timestamp:O}}","message":"{{message}}"}"""],
            _ => [$" warn    [{timestamp:O}] {message}"]
        };
    }

    [Theory]
    [InlineData("2026-11-01T01:50:00-04:00", "2026-11-01T01:10:00-05:00", false)]
    [InlineData("2026-11-01T01:10:00-05:00", "2026-11-01T01:50:00-04:00", true)]
    public void Filter_ExplicitOffsetsAcrossRepeatedHour_OrdersByInstant(string eventTime, string since, bool included)
    {
        var cutoff = LogSinceFilter.ParseSinceOption(since)!.Value;
        var line = $$"""{"timestamp":"{{eventTime}}"}""";
        var result = LogSinceFilter.Filter([line], cutoff.AddHours(-2), cutoff);

        Assert.Equal(included ? [line] : Array.Empty<string>(), result);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("2d", 172800)]
    public void ParseSinceOption_Duration_UsesElapsedTime(string since, int seconds)
    {
        var earliest = DateTimeOffset.UtcNow.AddSeconds(-seconds);
        var result = LogSinceFilter.ParseSinceOption(since);
        var latest = DateTimeOffset.UtcNow.AddSeconds(-seconds);

        Assert.NotNull(result);
        Assert.InRange(result.Value, earliest, latest);
    }

    [Fact]
    public async Task ReadLinesAsync_RequestFinishesAfterCutoff_FiltersByEventTimeAcrossReads()
    {
        var path = Path.GetTempFileName();
        try
        {
            var cutoff = _start.AddHours(1);
            var oldRequest = RequestBlock("https://example.com/old", cutoff.AddSeconds(-1));
            var newRequest = RequestBlock("https://example.com/new", cutoff);
            await File.WriteAllTextAsync(path, string.Join('\n', oldRequest[..2]) + "\n");
            using var reader = new LogFileReader(path, _start, cutoff);

            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, string.Join('\n', oldRequest[2..]) + "\n");
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, string.Join('\n', newRequest[..3]) + "\n");
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, string.Join('\n', newRequest[3..]) + "\n");
            Assert.Equal(newRequest, await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadLinesAsync_PartialLinesAndMidnight_RetainsReconstructionState()
    {
        var path = Path.GetTempFileName();
        try
        {
            var beforeMidnight = " dbug    [23:59:59.0000000] before midnight";
            var afterMidnight = " dbug    [00:00:01.0000000] after midnight";
            var cutoff = _start.Date.AddDays(1);
            await File.WriteAllTextAsync(path, beforeMidnight + "\r\n");
            using var reader = new LogFileReader(path, _start, cutoff);

            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, afterMidnight[..20]);
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));

            await File.AppendAllTextAsync(path, afterMidnight[20..] + "\r\n");
            Assert.Equal([afterMidnight], await reader.ReadLinesAsync(CancellationToken.None));
            Assert.Empty(await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadLinesAsync_JsonLines_UsesEventTimeInsteadOfReadTime()
    {
        var path = Path.GetTempFileName();
        try
        {
            var cutoff = _start.AddHours(1);
            var oldLine = $$"""{"timestamp":"{{new DateTimeOffset(_start):O}}","message":"old"}""";
            var newLine = $$"""{"timestamp":"{{new DateTimeOffset(cutoff):O}}","message":"new"}""";
            using var reader = new LogFileReader(path, _start, cutoff);

            await File.AppendAllTextAsync(path, oldLine + "\n" + newLine + "\n");

            Assert.Equal([newLine], await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadLinesAsync_NoSince_EmitsLinesWithoutWaitingForRequestEnd()
    {
        var path = Path.GetTempFileName();
        try
        {
            string[] lines = ["", " req   \u256d GET https://example.com", " info    hello"];
            await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n");
            using var reader = new LogFileReader(path, _start, null);

            Assert.Equal(lines, await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FilterLines_NoSince_ReturnsAllLines()
    {
        string[] lines = ["a", "b"];

        Assert.Equal(lines, Commands.LogsCommand.FilterLines(lines, null, _start));
    }
}
