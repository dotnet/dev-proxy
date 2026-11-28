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

public class ProxyConsoleFormatterTests
{
    [Fact]
    public void FinishedProcessingRequest_WriterThrows_ReleasesBufferedLines()
    {
        var formatter = new ProxyConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions()));
        var scopeProvider = new LoggerExternalScopeProvider();
        using var scope = scopeProvider.Push(new Dictionary<string, object> { ["requestId"] = 1 });

        Write(formatter, scopeProvider, new StringWriter(CultureInfo.InvariantCulture), "GET https://first", MessageType.InterceptedRequest);
        _ = Assert.Throws<IOException>(() =>
            Write(formatter, scopeProvider, new ThrowingWriter(), string.Empty, MessageType.FinishedProcessingRequest));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(formatter, scopeProvider, writer, "GET https://second", MessageType.InterceptedRequest);
        Write(formatter, scopeProvider, writer, string.Empty, MessageType.FinishedProcessingRequest);

        var output = writer.ToString();
        Assert.Contains("https://second", output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://first", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LogLevel.Information, "Base URL: http://127.0.0.1:8897")]
    [InlineData(LogLevel.Warning, "warning: Base URL: http://127.0.0.1:8897")]
    [InlineData(LogLevel.Error, "error: Base URL: http://127.0.0.1:8897")]
    [InlineData(LogLevel.Critical, "error: Base URL: http://127.0.0.1:8897")]
    public void PlainOutput_WritesMessageWithoutLevelLabel(LogLevel level, string expected)
    {
        var formatter = new ProxyConsoleFormatter(Options.Create(new ProxyConsoleFormatterOptions { PlainOutput = true }));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var entry = new LogEntry<string>(level, "Tests", new EventId(0), "Base URL: http://127.0.0.1:8897", null, (state, _) => state);

        formatter.Write(entry, null, writer);

        Assert.Equal(expected + Environment.NewLine, writer.ToString());
    }

    private static void Write(ProxyConsoleFormatter formatter, IExternalScopeProvider scopeProvider, TextWriter writer, string message, MessageType type)
    {
        var log = new RequestLog(message, type, context: null);
        var entry = new LogEntry<RequestLog>(LogLevel.Information, ProxyConsoleFormatter.DefaultCategoryName, new EventId(0), log, null, (state, _) => state.Message);
        formatter.Write(entry, scopeProvider, writer);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public ThrowingWriter() : base(CultureInfo.InvariantCulture) { }

        public override void Write(string? value) => throw new IOException("Console unavailable");

        public override void WriteLine(string? value) => throw new IOException("Console unavailable");
    }
}
