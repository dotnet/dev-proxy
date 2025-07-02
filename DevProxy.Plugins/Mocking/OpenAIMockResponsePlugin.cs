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
using Titanium.Web.Proxy.Models;

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

    public override async Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (e.ResponseState.HasBeenSet)
        {
            Logger.LogRequest("Response already set", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new(e.Session));
            return;
        }

        if (!TryGetOpenAIRequest(request.BodyString, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new(e.Session));
            return;
        }

        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            if ((await languageModelClient.GenerateCompletionAsync(completionRequest.Prompt, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAICompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else if (openAiRequest is OpenAIChatCompletionRequest chatRequest)
        {
            if ((await languageModelClient
                .GenerateChatCompletionAsync(chatRequest.Messages, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAIChatCompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

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

    private void SendMockResponse<TResponse>(OpenAIResponse response, string localLmUrl, ProxyRequestArgs e) where TResponse : OpenAIResponse
    {
        e.Session.GenericResponse(
            // we need this cast or else the JsonSerializer drops derived properties
            JsonSerializer.Serialize((TResponse)response, ProxyUtils.JsonSerializerOptions),
            HttpStatusCode.OK,
            [
                new HttpHeader("content-type", "application/json"),
                new HttpHeader("access-control-allow-origin", "*")
            ]
        );
        e.ResponseState.HasBeenSet = true;
        Logger.LogRequest($"200 {localLmUrl}", MessageType.Mocked, new(e.Session));
    }
}
