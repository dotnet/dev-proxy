using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Prompty;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Behavior;

public class LanguageModelFailureConfiguration
{
    public IEnumerable<string>? Failures { get; set; }
}

public sealed class LanguageModelFailurePlugin(
    HttpClient httpClient,
    ILogger<LanguageModelFailurePlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BasePlugin<LanguageModelFailureConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private readonly string[] _defaultFailures = [
        "AmbiguityVagueness",
        "BiasStereotyping",
        "CircularReasoning",
        "ContradictoryInformation",
        "FailureDisclaimHedge",
        "FailureFollowInstructions",
        "Hallucination",
        "IncorrectFormatStyle",
        "Misinterpretation",
        "OutdatedInformation",
        "OverSpecification",
        "OverconfidenceUncertainty",
        "Overgeneralization",
        "OverreliancePriorConversation",
        "PlausibleIncorrect",
    ];

    public override string Name => nameof(LanguageModelFailurePlugin);

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

        var (faultName, faultPrompt) = GetFault();
        if (faultPrompt is null)
        {
            Logger.LogError("Failed to get fault prompt. Passing request as-is.");
            return;
        }

        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            completionRequest.Prompt += "\n\n" + faultPrompt;
            Logger.LogDebug("Modified completion request prompt: {Prompt}", completionRequest.Prompt);
            Logger.LogRequest($"Simulating fault {faultName}", MessageType.Chaos, new(e.Session));
            e.Session.SetRequestBodyString(JsonSerializer.Serialize(completionRequest, ProxyUtils.JsonSerializerOptions));
        }
        else if (openAiRequest is OpenAIChatCompletionRequest chatRequest)
        {
            var messages = new List<OpenAIChatCompletionMessage>(chatRequest.Messages)
            {
                new()
                {
                    Role = "user",
                    Content = faultPrompt
                }
            };
            var newRequest = new OpenAIChatCompletionRequest
            {
                Model = chatRequest.Model,
                Stream = chatRequest.Stream,
                Temperature = chatRequest.Temperature,
                TopP = chatRequest.TopP,
                Messages = messages
            };

            Logger.LogDebug("Added fault prompt to messages: {Prompt}", faultPrompt);
            Logger.LogRequest($"Simulating fault {faultName}", MessageType.Chaos, new(e.Session));
            e.Session.SetRequestBodyString(JsonSerializer.Serialize(newRequest, ProxyUtils.JsonSerializerOptions));
        }
        else if (openAiRequest is OpenAIResponsesRequest responsesRequest)
        {
            // Handle Responses API
            if (responsesRequest.Input is string inputString)
            {
                // Simple string input - append fault prompt
                responsesRequest.Input = inputString + "\n\n" + faultPrompt;
                Logger.LogDebug("Modified Responses API string input with fault prompt");
            }
            else if (responsesRequest.Input is JsonElement inputElement)
            {
                // Structured input - append as new message item
                try
                {
                    var items = JsonSerializer.Deserialize<List<JsonElement>>(inputElement.GetRawText(), ProxyUtils.JsonSerializerOptions) ?? [];
                    var faultItem = JsonSerializer.SerializeToElement(new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "input_text", text = faultPrompt }
                        }
                    }, ProxyUtils.JsonSerializerOptions);
                    items.Add(faultItem);
                    responsesRequest.Input = items;
                    Logger.LogDebug("Added fault prompt as new item to Responses API input");
                }
                catch (JsonException)
                {
                    // If we can't parse as array, append to input as string
                    responsesRequest.Input = inputElement.GetRawText() + "\n\n" + faultPrompt;
                    Logger.LogDebug("Modified Responses API input with fault prompt (fallback)");
                }
            }

            Logger.LogRequest($"Simulating fault {faultName}", MessageType.Chaos, new(e.Session));
            e.Session.SetRequestBodyString(JsonSerializer.Serialize(responsesRequest, ProxyUtils.JsonSerializerOptions));
        }
        else
        {
            Logger.LogDebug("Unknown OpenAI request type. Passing request as-is.");
        }

        await Task.CompletedTask;

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
    }

    private (string? Name, string? Prompt) GetFault()
    {
        var failures = Configuration.Failures?.ToArray() ?? _defaultFailures;
        if (failures.Length == 0)
        {
            Logger.LogWarning("No failures configured. Using default failures.");
            failures = _defaultFailures;
        }

        var random = new Random();
        var randomFailure = failures[random.Next(failures.Length)];
        Logger.LogDebug("Selected random failure: {Failure}", randomFailure);

        // convert failure to the prompt file name; PascalCase to kebab-case
        var promptFileName = $"lmfailure_{randomFailure.ToKebabCase()}.prompty";

        var promptFilePath = Path.Combine(ProxyUtils.AppFolder ?? string.Empty, "prompts", promptFileName);
        if (!File.Exists(promptFilePath))
        {
            Logger.LogError("Prompt file {PromptFile} does not exist.", promptFilePath);
            return (null, null);
        }

        var promptContents = File.ReadAllText(promptFilePath);

        try
        {
            var prompty = Prompt.FromMarkdown(promptContents);
            if (prompty.Prepare(null, true) is not IEnumerable<ChatMessage> promptyMessages ||
                !promptyMessages.Any())
            {
                Logger.LogError("No messages found in the prompt file: {FilePath}", promptFilePath);
                return (null, null);
            }

            // last message in the prompty file is the fault prompt
            return (randomFailure, promptyMessages.ElementAt(^1).Text);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load or parse the prompt file: {FilePath}", promptFilePath);
            return (null, null);
        }
    }
}