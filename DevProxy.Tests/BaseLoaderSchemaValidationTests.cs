// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace DevProxy.Tests;

public sealed class BaseLoaderSchemaValidationTests : IDisposable
{
    private const string SchemaUrl = "https://schemas.example.test/mocks.schema.json";
    private const string Schema = """{ "properties": { "name": { "type": "string" } } }""";
    // Doesn't match the schema, which requires name to be a string
    private const string Contents = $$"""{ "$schema": "{{SchemaUrl}}", "name": 1 }""";
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-loader").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task LoadsFileWithWarningWhenSchemaCannotBeRetrieved()
    {
        var logger = new RecordingLogger();

        var loader = await LoadAsync(_ => throw new HttpRequestException("Connection refused"), logger);

        Assert.NotNull(loader.Contents);
        Assert.Contains(logger.Messages, m => m.LogLevel == LogLevel.Warning && m.Message.Contains("Connection refused", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.LogLevel >= LogLevel.Error);
    }

    [Fact]
    public async Task DoesNotLoadFileThatDoesNotMatchSchema()
    {
        var logger = new RecordingLogger();

        var loader = await LoadAsync(_ => new(HttpStatusCode.OK) { Content = new StringContent(Schema) }, logger);

        Assert.Null(loader.Contents);
        Assert.Contains(logger.Messages, m => m.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task DoesNotLoadFileWhenCanceledWhileRetrievingSchema()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loader = await LoadAsync(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, logger, cts.Token);

        Assert.Null(loader.Contents);
        Assert.DoesNotContain(logger.Messages, m => m.LogLevel >= LogLevel.Warning);
    }

    private async Task<TestLoader> LoadAsync(Func<HttpRequestMessage, HttpResponseMessage> sendSchemaRequest, ILogger logger, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_folder, "mocks.json");
        await File.WriteAllTextAsync(path, Contents, CancellationToken.None);
        using var handler = new FakeHttpMessageHandler(sendSchemaRequest);
        using var httpClient = new HttpClient(handler);
        var loader = new TestLoader(httpClient, logger, path);
        await loader.InitFileWatcherAsync(cancellationToken);
        loader.Dispose();
        return loader;
    }

    private sealed class TestLoader(HttpClient httpClient, ILogger logger, string filePath) :
        BaseLoader(httpClient, logger, new FakeProxyConfiguration { ValidateSchemas = true })
    {
        public string? Contents { get; private set; }
        protected override string FilePath => filePath;
        protected override void LoadData(string fileContents) => Contents = fileContents;
    }
}
