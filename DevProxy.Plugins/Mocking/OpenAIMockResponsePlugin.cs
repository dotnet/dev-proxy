// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace DevProxy.Plugins.Mocking;

public sealed class OpenAIMockResponsePlugin(
    ILogger<OpenAIMockResponsePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    ILanguageModelClient languageModelClient) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(OpenAIMockResponsePlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        Logger.LogInformation("Checking language model availability...");
        if (!await languageModelClient.IsEnabledAsync(cancellationToken))
        {
            Logger.LogError("Local language model is not enabled. The {Plugin} will not be used.", Name);
            Enabled = false;
        }
    }

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (args.Request.Method != HttpMethod.Post || args.Request.Content is null)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        var requestBody = await args.Request.Content.ReadAsStringAsync(cancellationToken);
        if (!TryGetOpenAIRequest(requestBody, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, args.Request);
            return PluginResponse.Continue();
        }

        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            if ((await languageModelClient.GenerateCompletionAsync(completionRequest.Prompt, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return PluginResponse.Continue();
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return PluginResponse.Continue();
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            var httpResponse = CreateMockResponse<OpenAICompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, args.Request);
            return PluginResponse.Respond(httpResponse);
        }
        else if (openAiRequest is OpenAIChatCompletionRequest chatRequest)
        {
            if ((await languageModelClient
                .GenerateChatCompletionAsync(chatRequest.Messages, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return PluginResponse.Continue();
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return PluginResponse.Continue();
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            var httpResponse = CreateMockResponse<OpenAIChatCompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, args.Request);
            return PluginResponse.Respond(httpResponse);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
            return PluginResponse.Continue();
        }
    };

    private bool TryGetOpenAIRequest(string content, out OpenAIRequest? request)
    {
        request = null;

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            Logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            if (rawRequest.TryGetProperty("prompt", out _))
            {
                Logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            if (rawRequest.TryGetProperty("messages", out _))
            {
                Logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            Logger.LogDebug("Request is not an OpenAI request.");
            return false;
        }
        catch (JsonException ex)
        {
            Logger.LogDebug(ex, "Failed to deserialize OpenAI request.");
            return false;
        }
    }

    private HttpResponseMessage CreateMockResponse<TResponse>(OpenAIResponse response, string localLmUrl, HttpRequestMessage originalRequest) where TResponse : OpenAIResponse
    {
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                // we need this cast or else the JsonSerializer drops derived properties
                JsonSerializer.Serialize((TResponse)response, ProxyUtils.JsonSerializerOptions),
                System.Text.Encoding.UTF8,
                "application/json"
            )
        };

        httpResponse.Headers.Add("access-control-allow-origin", "*");

        Logger.LogRequest($"200 {localLmUrl}", MessageType.Mocked, originalRequest);
        return httpResponse;
    }
}
