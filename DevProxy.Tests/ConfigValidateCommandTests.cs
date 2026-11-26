// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using DevProxy.Commands;
using DevProxy.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Tests;

public sealed class ConfigValidateCommandTests : IDisposable
{
    private readonly string _configFile = Path.Combine(Path.GetTempPath(), $"devproxy-validate-{Guid.NewGuid():N}.json");

    public ConfigValidateCommandTests() =>
        File.WriteAllText(_configFile, """{ "plugins": [], "urlsToWatch": [ "https://example.com/*" ] }""");

    public void Dispose() => File.Delete(_configFile);

    [Fact]
    public async Task Validate_JsonOutput_EmitsResultAsStructuredOutput()
    {
        var logger = new CapturingLogger();
        using var httpClient = new HttpClient();

        var exitCode = await ConfigCommand.ValidateConfigCoreAsync(_configFile, true, httpClient, logger, CancellationToken.None);

        Assert.Equal(2, exitCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogEvents.StructuredOutput, entry.EventId);
        using var result = JsonDocument.Parse(entry.Message);
        Assert.Equal(JsonValueKind.Object, result.RootElement.ValueKind);
        Assert.False(result.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(_configFile, result.RootElement.GetProperty("configFile").GetString());
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId, formatter(state, exception)));
    }
}
