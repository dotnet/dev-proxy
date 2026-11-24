// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Abstractions.Tests.Utils;

public class ProxyUtilsTests
{
    private const string SchemaUrl = "https://schemas.example.test/schema.json";
    private const string Schema = """{ "properties": { "name": { "type": "string" } } }""";

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_SchemaDownloadFails_ReturnsSchemaUnavailable()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);

        var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync("""{ "name": 1 }""", SchemaUrl, httpClient, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(SchemaValidationStatus.SchemaUnavailable, result.Status);
        Assert.Equal(["Connection refused"], result.Errors);
    }

    [Fact]
    public async Task ValidateJsonAgainstSchemaAsync_Canceled_Throws()
    {
        using var handler = new StubHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Schema) });
        using var httpClient = new HttpClient(handler);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProxyUtils.ValidateJsonAgainstSchemaAsync("""{ "name": 1 }""", SchemaUrl, httpClient, NullLogger.Instance, new CancellationToken(true)));
    }

    [Fact]
    public async Task ValidateJsonAsync_Canceled_ReturnsInvalid()
    {
        using var handler = new StubHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Schema) });
        using var httpClient = new HttpClient(handler);

        var (isValid, errors) = await ProxyUtils.ValidateJsonAsync("""{ "name": "a" }""", SchemaUrl, httpClient, NullLogger.Instance, new CancellationToken(true));

        Assert.False(isValid);
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404: Not Found")]
    [InlineData(HttpStatusCode.OK, "<html><body>Sign in to continue</body></html>")]
    public async Task ValidateJsonAgainstSchemaAsync_ResponseIsNotSchema_ReturnsSchemaUnavailable(HttpStatusCode statusCode, string content)
    {
        using var handler = new StubHttpMessageHandler(_ => new(statusCode) { Content = new StringContent(content) });
        using var httpClient = new HttpClient(handler);

        var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync("""{ "name": 1 }""", SchemaUrl, httpClient, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(SchemaValidationStatus.SchemaUnavailable, result.Status);
    }

    [Theory]
    [InlineData("""{ "name": "Contoso" }""", SchemaValidationStatus.Valid)]
    [InlineData("""{ "name": 1 }""", SchemaValidationStatus.Invalid)]
    public async Task ValidateJsonAgainstSchemaAsync_SchemaRetrieved_ValidatesJson(string json, SchemaValidationStatus expectedStatus)
    {
        using var handler = new StubHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Schema) });
        using var httpClient = new HttpClient(handler);

        var result = await ProxyUtils.ValidateJsonAgainstSchemaAsync(json, SchemaUrl, httpClient, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Theory]
    [InlineData("wss://ws.example.test/socket", "https://ws.example.test/socket")]
    [InlineData("ws://ws.example.test/socket", "http://ws.example.test/socket")]
    [InlineData("WSS://ws.example.test/*", "https://ws.example.test/*")]
    [InlineData("Ws://ws.example.test/*", "http://ws.example.test/*")]
    public void NormalizeWebSocketScheme_RewritesWebSocketSchemes(string input, string expected) =>
        Assert.Equal(expected, ProxyUtils.NormalizeWebSocketScheme(input));

    [Theory]
    [InlineData("https://api.contoso.com/v1")]
    [InlineData("http://api.contoso.com/v1")]
    [InlineData("api.contoso.com/*")]
    [InlineData("")]
    public void NormalizeWebSocketScheme_LeavesOtherSchemesUntouched(string input) =>
        Assert.Equal(input, ProxyUtils.NormalizeWebSocketScheme(input));

    [Fact]
    public void NormalizeWebSocketScheme_DoesNotRewriteSchemeInPath() =>
        // only a leading ws(s):// is a scheme; an embedded one must survive
        Assert.Equal(
            "https://host/redirect?to=ws://other",
            ProxyUtils.NormalizeWebSocketScheme("https://host/redirect?to=ws://other"));

    [Fact]
    public void NormalizeWebSocketScheme_NullThrows() =>
        Assert.Throws<System.ArgumentNullException>(() => ProxyUtils.NormalizeWebSocketScheme(null!));

    [Fact]
    public void ReplacePathTokens_ExpandsLeadingTildeSlashToHomeDirectory()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(System.IO.Path.Join(home, "mocks/mocks.json"), ProxyUtils.ReplacePathTokens("~/mocks/mocks.json"));
    }

    [Fact]
    public void ReplacePathTokens_ExpandsTildeSlashOnlyToHomeDirectory()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, ProxyUtils.ReplacePathTokens("~/"));
    }

    [Fact]
    public void ReplacePathTokens_ExpandsLeadingTildeBackslashOnWindowsOnly()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var expected = System.OperatingSystem.IsWindows()
            ? System.IO.Path.Join(home, @"mocks\mocks.json")
            : @"~\mocks\mocks.json";
        Assert.Equal(expected, ProxyUtils.ReplacePathTokens(@"~\mocks\mocks.json"));
    }

    [Theory]
    [InlineData("~mocks/mocks.json")]
    [InlineData("mocks/~/mocks.json")]
    [InlineData("~")]
    public void ReplacePathTokens_DoesNotExpandNonLeadingOrBareTilde(string input) =>
        Assert.Equal(input, ProxyUtils.ReplacePathTokens(input));

    [Fact]
    public void GetFullPath_ResolvesTildeSlashToHomeDirectoryInsteadOfConfigFolder()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var configFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devproxyrc.json");
        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(home, "mocks", "mocks.json")),
            ProxyUtils.GetFullPath("~/mocks/mocks.json", configFile));
    }

    [Fact]
    public void SerializeBody_NullReturnsEmptyString() =>
        Assert.Equal(string.Empty, ProxyUtils.SerializeBody(null));

    [Fact]
    public void SerializeBody_StringIsReturnedAsIs() =>
        Assert.Equal("hello", ProxyUtils.SerializeBody("hello"));

    [Fact]
    public void SerializeBody_JsonStringElementIsReturnedAsIs() =>
        Assert.Equal("hello", ProxyUtils.SerializeBody(JsonSerializer.Deserialize<object>("\"hello\"")));

    [Fact]
    public void SerializeBody_ObjectIsSerializedAsJson()
    {
        var body = ProxyUtils.SerializeBody(JsonSerializer.Deserialize<object>("""{ "message": "hello" }"""));

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("hello", doc.RootElement.GetProperty("message").GetString());
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
