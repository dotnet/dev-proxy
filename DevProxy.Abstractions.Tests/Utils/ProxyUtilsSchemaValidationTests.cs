// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Abstractions.Tests.Utils;

public sealed class ProxyUtilsSchemaValidationTests : IDisposable
{
    private const string CurrentVersion = "4.1.0";
    private const string SchemaFile = "test.schema.json";
    private const string SchemaUrl = $"https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v{CurrentVersion}/{SchemaFile}";
    private const string RequireNameSchema = """{ "type": "object", "required": ["name"] }""";
    private const string AllowAnythingSchema = """{ "type": "object" }""";
    private const string JsonWithoutName = """{ "other": 1 }""";

    private readonly string _schemasFolder = Directory.CreateTempSubdirectory("devproxy-schemas-").FullName;

    public void Dispose() => Directory.Delete(_schemasFolder, true);

    [Theory]
    [InlineData(SchemaUrl, true)]
    [InlineData("https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/V4.1.0/test.schema.json", true)]
    [InlineData("https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v4.0.0/test.schema.json", false)]
    [InlineData("https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v4.1.0/missing.schema.json", false)]
    [InlineData("https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v4.1.0/../test.schema.json", false)]
    [InlineData("https://example.com/schemas/v4.1.0/test.schema.json", false)]
    [InlineData("", false)]
    public void GetLocalSchemaPath_MapsOnlyCurrentVersionDevProxySchemas(string schemaUrl, bool expectLocal)
    {
        WriteLocalSchema(AllowAnythingSchema);

        var localPath = ProxyUtils.GetLocalSchemaPath(schemaUrl, CurrentVersion, _schemasFolder);

        if (expectLocal)
        {
            Assert.Equal(Path.Combine(_schemasFolder, SchemaFile), localPath);
        }
        else
        {
            Assert.Null(localPath);
        }
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_LocalSchemaValid_DoesNotDownloadSchema()
    {
        WriteLocalSchema(AllowAnythingSchema);
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP call"));

        var (isValid, errors) = await ValidateAsync(JsonWithoutName, SchemaUrl, handler);

        Assert.True(isValid);
        Assert.Empty(errors);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_LocalSchemaInvalid_UsesRemoteSchemaResult()
    {
        WriteLocalSchema(RequireNameSchema);
        using var handler = new FakeHandler(_ => Ok(AllowAnythingSchema));

        var (isValid, _) = await ValidateAsync(JsonWithoutName, SchemaUrl, handler);

        Assert.True(isValid);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_LocalSchemaInvalidAndRemoteUnavailable_UsesLocalResult()
    {
        WriteLocalSchema(RequireNameSchema);
        using var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));

        var (isValid, errors) = await ValidateAsync(JsonWithoutName, SchemaUrl, handler);

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("name", StringComparison.Ordinal));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_LocalSchemaInvalidAndRemoteTimesOut_UsesLocalResult()
    {
        WriteLocalSchema(RequireNameSchema);
        using var handler = new FakeHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return await Ok(AllowAnythingSchema);
        });

        var (isValid, _) = await ValidateAsync(JsonWithoutName, SchemaUrl, handler, TimeSpan.FromMilliseconds(50));

        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_NoLocalSchema_DownloadsSchema()
    {
        const string otherVersionUrl = "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v4.0.0/test.schema.json";
        WriteLocalSchema(AllowAnythingSchema);
        using var handler = new FakeHandler(_ => Ok(RequireNameSchema));

        var (isValid, _) = await ValidateAsync(JsonWithoutName, otherVersionUrl, handler);

        Assert.False(isValid);
        Assert.Equal(1, handler.CallCount);
    }

    private void WriteLocalSchema(string contents) =>
        File.WriteAllText(Path.Combine(_schemasFolder, SchemaFile), contents);

    private async Task<(bool IsValid, IEnumerable<string> ValidationErrors)> ValidateAsync(string json, string schemaUrl, FakeHandler handler, TimeSpan? timeout = null)
    {
        using var httpClient = new HttpClient(handler, false);
        var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync(json, schemaUrl, httpClient, NullLogger.Instance, _schemasFolder, CurrentVersion, timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);
        return (result.Status == SchemaValidationStatus.Valid, result.Errors);
    }

    private static Task<HttpResponseMessage> Ok(string content) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });

    private sealed class FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return respond(cancellationToken);
        }
    }
}
