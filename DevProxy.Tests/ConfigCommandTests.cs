// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;

namespace DevProxy.Tests;

public sealed class ConfigCommandTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-config").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task ValidateConfigCoreAsync_SchemaCannotBeRetrieved_ReportsWarningInsteadOfError()
    {
        var configFile = Path.Combine(_folder, "devproxyrc.json");
        await File.WriteAllTextAsync(configFile, """{ "$schema": "https://schemas.example.test/rc.schema.json", "plugins": [] }""");
        var logger = new RecordingLogger();
        using var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);

        _ = await ConfigCommand.ValidateConfigCoreAsync(configFile, isJsonOutput: true, httpClient, logger, CancellationToken.None);

        using var result = JsonDocument.Parse(Assert.Single(logger.Messages, m => m.LogLevel == LogLevel.Information).Message);
        Assert.DoesNotContain(result.RootElement.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "schema");
        Assert.Contains(result.RootElement.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("path").GetString() == "$schema" &&
                w.GetProperty("message").GetString()!.Contains("Connection refused", StringComparison.Ordinal));
    }
}
