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

    public override Func<RequestArguments, CancellationToken, Task<PluginResponse>>? OnRequestAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request, args.RequestId);
            return PluginResponse.Continue();
        }

        if (args.Request.Method != HttpMethod.Post || args.Request.Content == null)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, args.Request, args.RequestId);
            return PluginResponse.Continue();
        }

        var requestBody = await args.Request.Content.ReadAsStringAsync(cancellationToken);
        if (!TryGetOpenAIRequest(requestBody, out var openAiRequest))
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, args.Request, args.RequestId);
            return PluginResponse.Continue();
        }

        var (faultName, faultPrompt) = GetFault();
        if (faultPrompt is null)
        {
            Logger.LogError("Failed to get fault prompt. Passing request as-is.");
            return PluginResponse.Continue();
        }

        string modifiedRequestBody;
        if (openAiRequest is OpenAICompletionRequest completionRequest)
        {
            completionRequest.Prompt += "\n\n" + faultPrompt;
            Logger.LogDebug("Modified completion request prompt: {Prompt}", completionRequest.Prompt);
            Logger.LogRequest($"Simulating fault {faultName}", MessageType.Chaos, args.Request, args.RequestId);
            modifiedRequestBody = JsonSerializer.Serialize(completionRequest, ProxyUtils.JsonSerializerOptions);
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
            Logger.LogRequest($"Simulating fault {faultName}", MessageType.Chaos, args.Request, args.RequestId);
            modifiedRequestBody = JsonSerializer.Serialize(newRequest, ProxyUtils.JsonSerializerOptions);
        }
        else
        {
            Logger.LogDebug("Unknown OpenAI request type. Passing request as-is.");
            return PluginResponse.Continue();
        }

        // Create new request with modified body
        var modifiedRequest = new HttpRequestMessage(args.Request.Method, args.Request.RequestUri)
        {
            Content = new StringContent(modifiedRequestBody, System.Text.Encoding.UTF8, "application/json")
        };

        // Copy headers from original request
        foreach (var header in args.Request.Headers)
        {
            _ = modifiedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content headers if they exist
        if (args.Request.Content?.Headers != null)
        {
            foreach (var header in args.Request.Content.Headers)
            {
                if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    _ = modifiedRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestAsync));
        return PluginResponse.Continue(modifiedRequest);
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