// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using Xunit;

namespace DevProxy.Abstractions.Tests.LanguageModel;

public class OpenAILanguageModelClientTests
{
    private const string ChatCompletionResponse = """
        {"id":"1","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"yes"}}]}
        """;

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class CapturingLogger : ILogger<OpenAILanguageModelClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OpenAILanguageModelClient client, FakeHandler handler) CreateClient(
        LanguageModelConfiguration config,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
#pragma warning disable CA2000 // HttpClient lifetime is tied to the test
        var httpClient = new HttpClient(handler);
#pragma warning restore CA2000
        return (new OpenAILanguageModelClient(httpClient, config, NullLogger<OpenAILanguageModelClient>.Instance), handler);
    }

    private static LanguageModelConfiguration Config(LanguageModelClient client = LanguageModelClient.OpenAI, string? apiKey = null) => new()
    {
        Enabled = true,
        Client = client,
        Model = "gpt-4.1-mini",
        Url = "https://example.test/v1/",
        ApiKey = apiKey
    };

    [Fact]
    public async Task IsEnabled_ModelListed_DoesNotSendCompletion()
    {
        var (client, handler) = CreateClient(Config(apiKey: "secret"), _ =>
            Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}"""));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://example.test/v1/models", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task IsEnabled_OllamaTaggedModelListed_DoesNotSendCompletion()
    {
        var config = Config();
        config.Model = "llama3.2";
        var (client, handler) = CreateClient(config, _ =>
            Json(HttpStatusCode.OK, """{"data":[{"id":"llama3.2:latest"}]}"""));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        _ = Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task IsEnabled_Unauthorized_IsDisabled()
    {
        var (client, handler) = CreateClient(Config(apiKey: "wrong"), _ => Json(HttpStatusCode.Unauthorized, "{}"));

        Assert.False(await client.IsEnabledAsync(CancellationToken.None));
        _ = Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task IsEnabled_ModelsForbidden_FallsBackToCompletion()
    {
        var (client, handler) = CreateClient(Config(apiKey: "restricted"), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.Forbidden, "{}")
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task IsEnabled_FallbackCompletionFails_IsDisabled()
    {
        var (client, handler) = CreateClient(Config(), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.InternalServerError, "{}"));

        Assert.False(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[1]}""")]
    [InlineData("""{"data":[{"id":42}]}""")]
    public async Task IsEnabled_UnexpectedModelsPayload_FallsBackToCompletion(string payload)
    {
        var (client, handler) = CreateClient(Config(), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, payload)
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ErrorResponse_EchoingApiKey_IsRedactedInLogs()
    {
        var logger = new CapturingLogger();
        var handler = new FakeHandler(r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}""")
                : Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad key super-secret-key"}}"""));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAILanguageModelClient(httpClient, Config(apiKey: "super-secret-key"), logger);

        _ = await client.GenerateChatCompletionAsync(
            [new OpenAIChatCompletionMessage { Role = "user", Content = "hi" }], null, CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("bad key ***", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("super-secret-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsEnabled_ModelsNotSupported_FallsBackToCompletion()
    {
        var (client, handler) = CreateClient(Config(), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://example.test/v1/chat/completions", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task IsEnabled_ModelListedWithDifferentCase_FallsBackToCompletion()
    {
        var config = Config();
        config.Model = "GPT-4.1-mini";
        var (client, handler) = CreateClient(config, r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}""")
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task IsEnabled_ModelNotListed_FallsBackToCompletion()
    {
        var (client, handler) = CreateClient(Config(), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"data":[{"id":"other"}]}""")
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task IsEnabled_AzureOpenAI_UsesApiKeyHeaderAndSkipsModelMatch()
    {
        var (client, handler) = CreateClient(Config(LanguageModelClient.AzureOpenAI, "azure-secret"), _ =>
            Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1"}]}"""));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("azure-secret", Assert.Single(request.Headers.GetValues("api-key")));
    }

    [Fact]
    public async Task IsEnabled_AzureOpenAIWithoutApiKey_IsDisabled()
    {
        var (client, handler) = CreateClient(Config(LanguageModelClient.AzureOpenAI), _ => Json(HttpStatusCode.OK, "{}"));

        Assert.False(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApiKey_EnvReference_IsResolvedFromEnvironment()
    {
        var envVar = $"DEVPROXY_TEST_LM_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "from-env");
        try
        {
            var (client, handler) = CreateClient(Config(apiKey: $"@env:{envVar}"), _ =>
                Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}"""));

            Assert.True(await client.IsEnabledAsync(CancellationToken.None));
            Assert.Equal("from-env", handler.Requests[0].Headers.Authorization?.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task ApiKey_MissingEnvVar_SendsNoAuthHeader()
    {
        var (client, handler) = CreateClient(Config(apiKey: "@env:DEVPROXY_TEST_LM_KEY_DOES_NOT_EXIST"), _ =>
            Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}"""));

        Assert.True(await client.IsEnabledAsync(CancellationToken.None));
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task ChatCompletion_SendsAuthHeader()
    {
        var (client, handler) = CreateClient(Config(apiKey: "secret"), r =>
            r.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-4.1-mini"}]}""")
                : Json(HttpStatusCode.OK, ChatCompletionResponse));

        var response = await client.GenerateChatCompletionAsync(
            [new OpenAIChatCompletionMessage { Role = "user", Content = "hi" }], null, CancellationToken.None);

        Assert.Equal("yes", response?.Response);
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("secret", post.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ToString_DoesNotExposeApiKey()
    {
        Assert.DoesNotContain("secret", Config(apiKey: "secret").ToString(), StringComparison.Ordinal);
    }
}
