// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;

using Xunit;

namespace DevProxy.Tests;

public class LoggingConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureDevProxyLogging_Interactive_HonorsShowTimestamps(bool showTimestamps)
    {
        using var configuration = new ConfigurationManager();
        configuration["showTimestamps"] = showTimestamps.ToString();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ConfigureDevProxyLogging(configuration, new DevProxyConfigOptions()));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProxyConsoleFormatterOptions>>().Value;

        Assert.Equal(showTimestamps, options.ShowTimestamps);
        Assert.False(options.IncludeFullTimestamps);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ConfigureDevProxyLogging_UsesPlainOutputOnlyForSubcommands(bool isRootCommand, bool expectedPlainOutput)
    {
        using var configuration = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ConfigureDevProxyLogging(configuration, new DevProxyConfigOptions(), isStdioCommand: false, isJwtCommand: false, isRootCommand));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ProxyConsoleFormatterOptions>>().Value;

        Assert.Equal(expectedPlainOutput, options.PlainOutput);
    }

    [Theory]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public async Task Formatter_DetachedSurvivingRecord_RetainsFullTimestampForSnapshotsAndFollowers(LogLevel level)
    {
        var formatter = new ProxyConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions
        {
            ShowTimestamps = false,
            IncludeFullTimestamps = true
        }));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var entry = new LogEntry<string>(level, "Tests", new EventId(0), "surviving record", null, (state, _) => state);
        var cutoff = DateTimeOffset.UtcNow;

        formatter.Write(entry, null, writer);

        var line = writer.ToString().TrimEnd('\r', '\n');
        var timestamp = Assert.Single(LogSinceFilter.GetLineTimes([line], cutoff.AddHours(-1)));
        Assert.InRange(timestamp, cutoff, DateTimeOffset.UtcNow);
        Assert.Equal([line], LogSinceFilter.Filter([line], cutoff.AddHours(-1), cutoff));
        Assert.Empty(LogSinceFilter.Filter([line], cutoff.AddHours(-1), cutoff.AddHours(1)));

        var path = Path.GetTempFileName();
        try
        {
            using var reader = new LogFileReader(path, cutoff.AddHours(-1), cutoff);
            await File.AppendAllTextAsync(path, line + "\n");

            Assert.Equal([line], await reader.ReadLinesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Formatter_InteractiveWarning_DoesNotAddDetachedTimestampMetadata(bool showTimestamps)
    {
        var formatter = new ProxyConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions
        {
            ShowTimestamps = showTimestamps
        }));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var entry = new LogEntry<string>(LogLevel.Warning, "Tests", new EventId(0), "warning", null, (state, _) => state);

        formatter.Write(entry, null, writer);

        Assert.DoesNotMatch(@"\[\d{4}-\d{2}-\d{2}T", writer.ToString());
    }

    [Fact]
    public void JsonFormatter_BelowMinimumLevel_DropsDiagnosticsButKeepsStructuredOutput()
    {
        var formatter = new JsonConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions
        {
            MinimumLogLevel = LogLevel.Warning
        }));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var info = new LogEntry<string>(LogLevel.Information, "Tests", new EventId(0), "diagnostic", null, (state, _) => state);
        var result = new LogEntry<string>(LogLevel.Information, "Tests", LogEvents.StructuredOutput, "{\"ok\":true}", null, (state, _) => state);

        formatter.Write(info, null, writer);
        formatter.Write(result, null, writer);

        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains("\"type\":\"result\"", line, StringComparison.Ordinal);
    }
}
