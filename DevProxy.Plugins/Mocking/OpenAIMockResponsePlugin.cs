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

        if (!OpenAIRequest.TryGetOpenAIRequest(request.BodyString, Logger, out var openAiRequest))
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
        else if (openAiRequest is OpenAIResponsesRequest responsesRequest)
        {
            // Convert Responses API request to Chat Completion format for local LLM
            var messages = ConvertResponsesInputToMessages(responsesRequest);
            
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

            // Convert Chat Completion response to Responses API format
            var responsesResponse = ConvertToResponsesApiResponse(lmResponse, responsesRequest.Model);
            SendMockResponse<OpenAIResponsesResponse>(responsesResponse, lmResponse.RequestUrl ?? string.Empty, e);
        }
        else
        {
            Logger.LogError("Unknown OpenAI request type.");
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    private static List<OpenAIChatCompletionMessage> ConvertResponsesInputToMessages(OpenAIResponsesRequest responsesRequest)
    {
        var messages = new List<OpenAIChatCompletionMessage>();

        // Add instructions as system message if present
        if (!string.IsNullOrEmpty(responsesRequest.Instructions))
        {
            messages.Add(new OpenAIChatCompletionMessage
            {
                Role = "system",
                Content = responsesRequest.Instructions
            });
        }

        if (responsesRequest.Input is string inputString)
        {
            // Simple string input
            messages.Add(new OpenAIChatCompletionMessage
            {
                Role = "user",
                Content = inputString
            });
        }
        else if (responsesRequest.Input is JsonElement inputElement)
        {
            // Try to parse as array of items
            try
            {
                var items = JsonSerializer.Deserialize<List<JsonElement>>(inputElement.GetRawText(), ProxyUtils.JsonSerializerOptions);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.TryGetProperty("role", out var roleElement) &&
                            item.TryGetProperty("content", out var contentElement))
                        {
                            var role = roleElement.GetString() ?? "user";
                            var content = ExtractTextFromContent(contentElement);
                            
                            messages.Add(new OpenAIChatCompletionMessage
                            {
                                Role = role,
                                Content = content
                            });
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fallback: treat as simple text
                messages.Add(new OpenAIChatCompletionMessage
                {
                    Role = "user",
                    Content = inputElement.GetRawText()
                });
            }
        }

        return messages;
    }

    private static string ExtractTextFromContent(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }
        
        if (contentElement.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var item in contentElement.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textElement))
                {
                    texts.Add(textElement.GetString() ?? string.Empty);
                }
            }
            return string.Join("\n", texts);
        }

        return string.Empty;
    }

    private static OpenAIResponsesResponse ConvertToResponsesApiResponse(ILanguageModelCompletionResponse lmResponse, string model)
    {
        var chatResponse = (OpenAIChatCompletionResponse)lmResponse.ConvertToOpenAIResponse();
        
        var outputItems = new List<OpenAIResponsesOutputItem>();
        
        if (chatResponse.Choices != null && chatResponse.Choices.Any())
        {
            var choice = chatResponse.Choices.First();
            outputItems.Add(new OpenAIResponsesOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = new[]
                {
                    new OpenAIResponsesContentPart
                    {
                        Type = "output_text",
                        Text = choice.Message.Content
                    }
                }
            });
        }

        return new OpenAIResponsesResponse
        {
            Id = chatResponse.Id,
            Object = "response",
            Created = chatResponse.Created,
            CreatedAt = chatResponse.Created,
            Model = model,
            Status = "completed",
            Output = outputItems,
            Usage = chatResponse.Usage
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
        Logger.LogRequest($"200 {localLmUrl}", MessageType.Mocked, new(e.Session));
    }
}
