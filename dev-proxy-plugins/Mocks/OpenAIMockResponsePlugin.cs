// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Models;

namespace DevProxy.Plugins.Mocks;

public class OpenAIMockResponsePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(OpenAIMockResponsePlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        using var scope = Logger.BeginScope(Name);

        Logger.LogInformation("Checking language model availability...");
        if (!await Context.LanguageModelClient.IsEnabledAsync())
        {
            Logger.LogError("Local language model is not enabled. The {plugin} will not be used.", Name);
            return;
        }

        PluginEvents.BeforeRequest += OnRequestAsync;
    }

    private async Task OnRequestAsync(object sender, ProxyRequestArgs e)
    {
        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (!TryGetOpenAIRequest(request.BodyString, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            if ((await Context.LanguageModelClient.GenerateCompletionAsync(completionRequest.Prompt)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAICompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else if (openAiRequest is OpenAIChatCompletionRequest chatRequest)
        {
            if ((await Context.LanguageModelClient
                .GenerateChatCompletionAsync(chatRequest.Messages)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {error}", lmResponse.ErrorMessage);
                return;
            }

            var openAiResponse = lmResponse.ConvertToOpenAIResponse();
            SendMockResponse<OpenAIChatCompletionResponse>(openAiResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
        }
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
        Logger.LogRequest($"200 {localLmUrl}", MessageType.Mocked, new LoggingContext(e.Session));
    }
}
