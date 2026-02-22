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
    private const string ResponsesMessageIdPrefix = "msg_";

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
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (!OpenAIRequest.TryGetCompletionLikeRequest(request.BodyString, Logger, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
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
        else if (openAiRequest is OpenAIResponsesRequest responsesRequest)
        {
            // Convert Responses API input to chat completion messages
            var messages = ConvertResponsesInputToChatMessages(responsesRequest);
            if ((await languageModelClient
                .GenerateChatCompletionAsync(messages, null, cancellationToken)) is not ILanguageModelCompletionResponse lmResponse)
            {
                return;
            }
            if (lmResponse.ErrorMessage is not null)
            {
                Logger.LogError("Error from local language model: {Error}", lmResponse.ErrorMessage);
                return;
            }

            // Convert the chat completion response to Responses API format
            var responsesResponse = ConvertToResponsesResponse(lmResponse.ConvertToOpenAIResponse());
            SendMockResponse<OpenAIResponsesResponse>(responsesResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    private static IEnumerable<OpenAIChatCompletionMessage> ConvertResponsesInputToChatMessages(OpenAIResponsesRequest responsesRequest)
    {
        if (responsesRequest.Input is null)
        {
            return [];
        }

        return responsesRequest.Input.Select(item => new OpenAIChatCompletionMessage
        {
            Role = item.Role,
            Content = item.Content
        });
    }

    private static OpenAIResponsesResponse ConvertToResponsesResponse(OpenAIResponse chatResponse)
    {
        return new OpenAIResponsesResponse
        {
            Id = chatResponse.Id,
            Model = chatResponse.Model,
            Object = "response",
            Created = chatResponse.Created,
            CreatedAt = chatResponse.Created,
            Status = "completed",
            Usage = chatResponse.Usage,
            Output =
            [
                new OpenAIResponsesOutputItem
                {
                    Type = "message",
                    Id = $"{ResponsesMessageIdPrefix}{Guid.NewGuid():N}",
                    Role = "assistant",
                    Status = "completed",
                    Content =
                    [
                        new OpenAIResponsesOutputContent
                        {
                            Type = "output_text",
                            Text = chatResponse.Response
                        }
                    ]
                }
            ]
        };
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
