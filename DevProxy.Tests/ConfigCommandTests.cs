// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;
using DevProxy.Plugins.Behavior;
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
        // Copy only the plugin assembly so that its dependencies, like IPlugin,
        // resolve from the default load context as they do in a real install
        var pluginAssemblyPath = Path.Combine(_folder, Path.GetFileName(typeof(RetryAfterPlugin).Assembly.Location));
        File.Copy(typeof(RetryAfterPlugin).Assembly.Location, pluginAssemblyPath);
        var pluginPath = JsonSerializer.Serialize(pluginAssemblyPath);
        await File.WriteAllTextAsync(configFile, $$"""
            {
              "$schema": "https://schemas.example.test/rc.schema.json",
              "plugins": [{ "name": "RetryAfterPlugin", "enabled": true, "pluginPath": {{pluginPath}} }],
              "urlsToWatch": ["https://api.example.test/*"]
            }
            """, CancellationToken.None);
        var logger = new RecordingLogger();
        using var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);

        var exitCode = await ConfigCommand.ValidateConfigCoreAsync(configFile, isJsonOutput: true, httpClient, logger, CancellationToken.None);

        using var result = JsonDocument.Parse(Assert.Single(logger.Messages, m => m.LogLevel == LogLevel.Information).Message);
        Assert.Equal(0, exitCode);
        Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
        Assert.Empty(result.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Contains(result.RootElement.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("path").GetString() == "$schema" &&
                w.GetProperty("message").GetString()!.Contains("Connection refused", StringComparison.Ordinal));
    }
}
