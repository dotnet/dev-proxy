// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace DevProxy.Tests;

public sealed class BaseLoaderPlaceholderTests : IDisposable
{
    private const string Variable = "DEVPROXY_TEST_LOADER_PLACEHOLDER";
    private readonly string _folder = Directory.CreateTempSubdirectory("devproxy-loader").FullName;

    public BaseLoaderPlaceholderTests()
    {
        Environment.SetEnvironmentVariable(Variable, "resolved \"value\"");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        Directory.Delete(_folder, true);
    }

    [Theory]
    [InlineData("mocks.json", $$"""{ "body": "${{{Variable}}}", "missing": "${DEVPROXY_TEST_LOADER_MISSING}" }""")]
    [InlineData("mocks.yaml", $$"""
        body: ${{{Variable}}}
        missing: ${DEVPROXY_TEST_LOADER_MISSING}
        """)]
    public async Task LoadsFileWithResolvedPlaceholders(string fileName, string contents)
    {
        var loader = await LoadAsync(fileName, contents, resolve: true);

        using var doc = JsonDocument.Parse(loader.Contents!);
        Assert.Equal("resolved \"value\"", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("${DEVPROXY_TEST_LOADER_MISSING}", doc.RootElement.GetProperty("missing").GetString());
    }

    [Fact]
    public async Task LeavesPlaceholdersWhenResolutionIsDisabled()
    {
        var loader = await LoadAsync("rewrites.json", $$"""{ "url": "${{{Variable}}}" }""", resolve: false);

        using var doc = JsonDocument.Parse(loader.Contents!);
        Assert.Equal($"${{{Variable}}}", doc.RootElement.GetProperty("url").GetString());
    }

    private async Task<TestLoader> LoadAsync(string fileName, string contents, bool resolve)
    {
        var path = Path.Combine(_folder, fileName);
        await File.WriteAllTextAsync(path, contents);
        using var httpClient = new HttpClient();
        var loader = new TestLoader(httpClient, NullLogger.Instance, path, resolve);
        await loader.InitFileWatcherAsync(CancellationToken.None);
        loader.Dispose();
        return loader;
    }

    private sealed class TestLoader(HttpClient httpClient, ILogger logger, string filePath, bool resolve) :
        BaseLoader(httpClient, logger, new FakeProxyConfiguration())
    {
        public string? Contents { get; private set; }
        protected override string FilePath => filePath;
        protected override bool ResolveEnvironmentVariables => resolve;
        protected override void LoadData(string fileContents) => Contents = fileContents;
    }
}
