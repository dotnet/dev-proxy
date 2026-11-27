// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using DevProxy.Abstractions.Plugins;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevProxy.Tests;

public sealed class BaseLoaderSchemaValidationCacheTests : IDisposable
{
    private const string Variable = "DEVPROXY_TEST_LOADER_SCHEMA_NAME";
    private const string Schema = """
        {
          "type": "object",
          "properties": { "name": { "type": "string", "maxLength": 5 } },
          "required": ["name"]
        }
        """;
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-loader-schema").FullName;
    // unique per test so that the per-process schema and validation caches don't leak between tests
    private readonly string _schemaUrl = $"https://schemas.example.test/schema-{Guid.NewGuid()}.json";
    private readonly SchemaHandler _handler = new(Schema);
    private readonly HttpClient _httpClient;
    private readonly TestLogger _logger = new();

    public BaseLoaderSchemaValidationCacheTests() => _httpClient = new(_handler);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        _httpClient.Dispose();
        _handler.Dispose();
        Directory.Delete(_folder, true);
    }

    [Fact]
    public async Task DoesNotValidateUnchangedFileAgain()
    {
        var path = await WriteFileAsync("mocks.json", """{ "$schema": "{schema}", "name": "abc" }""");

        var first = await LoadAsync(path);
        var second = await LoadAsync(path);

        Assert.NotNull(first.Contents);
        Assert.NotNull(second.Contents);
        Assert.Equal(1, _logger.ValidationCount);
        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public async Task ValidatesChangedFileAgainWithCachedSchema()
    {
        var path = await WriteFileAsync("mocks.json", """{ "$schema": "{schema}", "name": "abc" }""");
        _ = await LoadAsync(path);

        _ = await WriteFileAsync("mocks.json", """{ "$schema": "{schema}", "name": "toolong" }""");
        var loader = await LoadAsync(path);

        Assert.Null(loader.Contents);
        Assert.Equal(2, _logger.ValidationCount);
        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public async Task DoesNotCacheFailedValidation()
    {
        var path = await WriteFileAsync("mocks.json", """{ "$schema": "{schema}", "name": "toolong" }""");

        var first = await LoadAsync(path);
        var second = await LoadAsync(path);

        Assert.Null(first.Contents);
        Assert.Null(second.Contents);
        Assert.Equal(2, _logger.ValidationCount);
    }

    [Fact]
    public async Task DoesNotLoadOrCacheInvalidJsonThatMatchesSchema()
    {
        var path = await WriteFileAsync("mocks.json", """{ "$schema": "{schema}", "name": 'abc' }""");

        var first = await LoadAsync(path);
        var second = await LoadAsync(path);

        Assert.Null(first.Contents);
        Assert.Null(second.Contents);
        Assert.Equal(2, _logger.ErrorCount);
    }

    [Fact]
    public async Task ValidatesAgainWhenResolvedPlaceholdersChange()
    {
        var path = await WriteFileAsync("mocks.json", $$"""{ "$schema": "{schema}", "name": "${{{Variable}}}" }""");

        Environment.SetEnvironmentVariable(Variable, "abc");
        var first = await LoadAsync(path);
        Environment.SetEnvironmentVariable(Variable, "toolong");
        var second = await LoadAsync(path);

        Assert.NotNull(first.Contents);
        Assert.Null(second.Contents);
        Assert.Equal(2, _logger.ValidationCount);
    }

    [Fact]
    public async Task DoesNotValidateUnchangedYamlFileAgain()
    {
        var path = await WriteFileAsync("mocks.yaml", """
            $schema: "{schema}"
            name: abc
            """);

        var first = await LoadAsync(path);
        var second = await LoadAsync(path);

        Assert.Contains("\"abc\"", first.Contents, StringComparison.Ordinal);
        Assert.NotNull(second.Contents);
        Assert.Equal(1, _logger.ValidationCount);
    }

    private async Task<string> WriteFileAsync(string fileName, string contents)
    {
        var path = Path.Combine(_folder, fileName);
        await File.WriteAllTextAsync(path, contents.Replace("{schema}", _schemaUrl, StringComparison.Ordinal));
        return path;
    }

    private async Task<TestLoader> LoadAsync(string path)
    {
        var loader = new TestLoader(_httpClient, _logger, path);
        await loader.InitFileWatcherAsync(CancellationToken.None);
        loader.Dispose();
        return loader;
    }

    private sealed class TestLoader(HttpClient httpClient, ILogger logger, string filePath) :
        BaseLoader(httpClient, logger, new FakeProxyConfiguration { ValidateSchemas = true })
    {
        public string? Contents { get; private set; }
        protected override string FilePath => filePath;
        protected override void LoadData(Stream fileContents)
        {
            using var reader = new StreamReader(fileContents);
            Contents = reader.ReadToEnd();
        }
    }

    private sealed class SchemaHandler(string schema) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(schema) });
        }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private int _errorCount;
        public int ValidationCount => _messages.Count(m => m.StartsWith("Validating JSON against schema", StringComparison.Ordinal));
        public int ErrorCount => _errorCount;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                _ = Interlocked.Increment(ref _errorCount);
            }
            _messages.Enqueue(formatter(state, exception));
        }
    }
}
