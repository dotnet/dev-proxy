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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppliesNewestDataWhenFileChangesWhileRetrievingSchema(bool schemaAvailable)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var initialContents = $$"""{ "$schema": "{{SchemaUrl}}", "name": "initial" }""";
        var updatedContents = $$"""{ "$schema": "{{SchemaUrl}}", "name": "updated" }""";
        var path = Path.Combine(_folder, "mocks.json");
        await File.WriteAllTextAsync(path, initialContents, CancellationToken.None);

        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstRequest = new SemaphoreSlim(0, 1);
        var requestCount = 0;
        using var handler = new AsyncHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                firstRequestStarted.SetResult();
                await releaseFirstRequest.WaitAsync();
            }
            return schemaAvailable
                ? new(HttpStatusCode.OK) { Content = new StringContent(Schema) }
                : throw new HttpRequestException("Connection refused");
        });
        using var httpClient = new HttpClient(handler);
        using var loader = new TestLoader(httpClient, new RecordingLogger(), path);

        var init = loader.InitFileWatcherAsync(CancellationToken.None);
        await firstRequestStarted.Task.WaitAsync(timeout);

        // While the initial load waits for the schema, the only FilePath access
        // comes from the reload triggered by the file change, right before it queues up.
        var reloadStarted = loader.WaitForFilePathAccessAsync();
        await File.WriteAllTextAsync(path, updatedContents, CancellationToken.None);
        await reloadStarted.WaitAsync(timeout);

        // The reload must wait for the initial load instead of reading the file.
        var reloadReadFile = loader.WaitForFilePathAccessAsync();
        using (var delayCancellation = new CancellationTokenSource())
        {
            _ = await Task.WhenAny(reloadReadFile, Task.Delay(TimeSpan.FromMilliseconds(500), delayCancellation.Token));
            await delayCancellation.CancelAsync();
        }
        Assert.False(reloadReadFile.IsCompleted);
        Assert.False(init.IsCompleted);

        _ = releaseFirstRequest.Release();
        await init.WaitAsync(timeout);
        var loadedOnInit = loader.LoadedContents;
        Assert.NotEmpty(loadedOnInit);
        Assert.Equal(initialContents, loadedOnInit[0]);

        await loader.WaitForLoadAsync(updatedContents).WaitAsync(timeout);
        Assert.Equal([initialContents, updatedContents], loader.LoadedContents);
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
        private readonly Lock _lock = new();
        private readonly List<string> _loadedContents = [];
        private readonly List<(string Contents, TaskCompletionSource Loaded)> _loadWaiters = [];
        private TaskCompletionSource? _filePathAccessed;

        public string? Contents { get; private set; }

        public string[] LoadedContents
        {
            get
            {
                lock (_lock)
                {
                    return [.. _loadedContents];
                }
            }
        }

        protected override string FilePath
        {
            get
            {
                _ = Interlocked.Exchange(ref _filePathAccessed, null)?.TrySetResult();
                return filePath;
            }
        }

        public Task WaitForFilePathAccessAsync()
        {
            var accessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _filePathAccessed = accessed;
            return accessed.Task;
        }

        public Task WaitForLoadAsync(string contents)
        {
            lock (_lock)
            {
                if (_loadedContents.Contains(contents))
                {
                    return Task.CompletedTask;
                }
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _loadWaiters.Add((contents, loaded));
                return loaded.Task;
            }
        }

        protected override void LoadData(string fileContents)
        {
            lock (_lock)
            {
                Contents = fileContents;
                _loadedContents.Add(fileContents);
                foreach (var (contents, loaded) in _loadWaiters.Where(w => w.Contents == fileContents))
                {
                    loaded.SetResult();
                }
                _ = _loadWaiters.RemoveAll(w => w.Contents == fileContents);
            }
        }
    }

    private sealed class AsyncHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
