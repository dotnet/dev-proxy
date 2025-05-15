// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;
using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

public class LanguageModelPricesPluginConfiguration
{
    public PricesData? Prices;
    public string? PricesFile { get; set; }
}

public class OpenAITelemetryPluginConfiguration : LanguageModelPricesPluginConfiguration
{
    public string Application { get; set; } = "default";
    public string Currency { get; set; } = "USD";
    public string Environment { get; set; } = "development";
    public string ExporterEndpoint { get; set; } = "http://localhost:4318";
    public bool IncludePrompt { get; set; } = true;
    public bool IncludeCompletion { get; set; } = true;
    public bool IncludeCosts { get; set; } = false;
}

public class OpenAITelemetryPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection), IDisposable
{
    public override string Name => nameof(OpenAITelemetryPlugin);
    private readonly OpenAITelemetryPluginConfiguration _configuration = new();
    private LanguageModelPricesLoader? _loader = null;

    private const string ActivitySourceName = "DevProxy.OpenAI";
    private const string OpenAISystem = "openai";
    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly static Meter _meter = new(ActivitySourceName);
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;

    private static Histogram<long>? _tokenUsageMetric;
    private static Histogram<double>? _requestCostMetric;
    private static Counter<double>? _totalCostMetric;

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        if (_configuration.IncludeCosts)
        {
            _configuration.PricesFile = Path.GetFullPath(ProxyUtils.ReplacePathTokens(_configuration.PricesFile), Path.GetDirectoryName(Context.Configuration.ConfigFile ?? string.Empty) ?? string.Empty);
            _loader = new LanguageModelPricesLoader(Logger, _configuration, Context.Configuration.ValidateSchemas);
            _loader.InitFileWatcher();
        }

        InitializeOpenTelemetryExporter();

        PluginEvents.BeforeRequest += OnRequestAsync;
        PluginEvents.AfterResponse += OnResponseAsync;
    }

    private void InitializeOpenTelemetryExporter()
    {
        Logger.LogTrace("InitializeOpenTelemetryExporter() called");

        try
        {
            var resourceBuilder = ResourceBuilder
                .CreateDefault()
                .AddService(serviceName: "DevProxy.OpenAI", serviceVersion: ProxyUtils.ProductVersion);

            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(options =>
                {
                    // We use protobuf to allow intercepting Dev Proxy's own LLM traffic
                    options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new Uri(_configuration.ExporterEndpoint + "/v1/traces");
                })
                .Build();

            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(ActivitySourceName)
                .AddView(SemanticConvention.GEN_AI_METRIC_CLIENT_TOKEN_USAGE, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864]
                })
                .AddView(SemanticConvention.GEN_AI_USAGE_COST, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 50, 100]
                })
                .AddView(SemanticConvention.GEN_AI_USAGE_TOTAL_COST, new MetricStreamConfiguration())
                .AddOtlpExporter(options =>
                {
                    // We use protobuf to allow intercepting Dev Proxy's own LLM traffic
                    options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new Uri(_configuration.ExporterEndpoint + "/v1/metrics");
                })
                .Build();

            _tokenUsageMetric = _meter.CreateHistogram<long>(
                SemanticConvention.GEN_AI_METRIC_CLIENT_TOKEN_USAGE,
                "tokens",
                "Number of tokens processed");
            _requestCostMetric = _meter.CreateHistogram<double>(
                SemanticConvention.GEN_AI_USAGE_COST,
                "cost",
                $"Estimated cost per request in {_configuration.Currency}");
            _totalCostMetric = _meter.CreateCounter<double>(
                SemanticConvention.GEN_AI_USAGE_TOTAL_COST,
                "cost",
                $"Total estimated cost for the session in {_configuration.Currency}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize OpenTelemetry exporter");
        }

        Logger.LogTrace("InitializeOpenTelemetryExporter() finished");
    }

    private async Task OnRequestAsync(object sender, ProxyRequestArgs e)
    {
        Logger.LogTrace("OnRequestAsync() called");

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        if (!TryGetOpenAIRequest(request.BodyString, out var openAiRequest) || openAiRequest is null)
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        // store for use in response
        e.SessionData["OpenAIRequest"] = openAiRequest;

        var activity = _activitySource.StartActivity(
            $"openai.{GetOperationName(openAiRequest)}",
            ActivityKind.Client);

        if (activity is null)
        {
            Logger.LogWarning("Failed to start OpenTelemetry activity for OpenAI request");
            return;
        }

        // add generic request tags
        activity.SetTag("http.method", request.Method);
        activity.SetTag("http.url", request.RequestUri.ToString());
        activity.SetTag("http.scheme", request.RequestUri.Scheme);
        activity.SetTag("http.host", request.RequestUri.Host);
        activity.SetTag("http.target", request.RequestUri.PathAndQuery);
        activity.SetTag(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem);
        activity.SetTag(SemanticConvention.GEN_AI_ENVIRONMENT, _configuration.Environment);
        activity.SetTag(SemanticConvention.GEN_AI_APPLICATION_NAME, _configuration.Application);

        AddCommonRequestTags(activity, openAiRequest);
        AddRequestTypeSpecificTags(activity, openAiRequest);

        // store for use in response
        e.SessionData["OpenAIActivity"] = activity;

        Logger.LogTrace("OnRequestAsync() finished");

        await Task.CompletedTask;
    }

    private async Task OnResponseAsync(object sender, ProxyResponseArgs e)
    {
        Logger.LogTrace("OnResponseAsync() called");

        if (!e.SessionData.TryGetValue("OpenAIActivity", out var activityObj) ||
            activityObj is not Activity activity)
        {
            return;
        }

        try
        {
            var response = e.Session.HttpClient.Response;

            activity.SetTag("http.status_code", response.StatusCode);

            switch (response.StatusCode)
            {
                case int code when code >= 200 && code < 300:
                    ProcessSuccessResponse(activity, e);
                    break;
                case int code when code >= 400:
                    ProcessErrorResponse(activity, e);
                    break;
            }
        }
        finally
        {
            activity.Stop();

            // Clean up session data
            e.SessionData.Remove("OpenAIActivity");
            e.SessionData.Remove("OpenAIRequest");

            Logger.LogRequest("OpenTelemetry information emitted", MessageType.Processed, new LoggingContext(e.Session));
        }

        await Task.CompletedTask;
    }

    private void ProcessErrorResponse(Activity activity, ProxyResponseArgs e)
    {
        Logger.LogTrace("ProcessErrorResponse() called");

        var response = e.Session.HttpClient.Response;

        activity.SetTag("error", true);
        activity.SetTag("error.type", "http");
        activity.SetTag("error.message", $"HTTP {response.StatusCode}");

        if (response.HasBody)
        {
            try
            {
                var errorObj = JsonSerializer.Deserialize<JsonElement>(response.BodyString);
                if (errorObj.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("message", out var message))
                        activity.SetTag("error.details", message.GetString());
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parsing errors in error responses
            }
        }

        Logger.LogTrace("ProcessErrorResponse() finished");
    }

    private void ProcessSuccessResponse(Activity activity, ProxyResponseArgs e)
    {
        Logger.LogTrace("ProcessSuccessResponse() called");

        var response = e.Session.HttpClient.Response;

        if (!response.HasBody || string.IsNullOrEmpty(response.BodyString))
        {
            Logger.LogDebug("Response body is empty or null");
            return;
        }

        if (!e.SessionData.TryGetValue("OpenAIRequest", out var requestObj) ||
            requestObj is not OpenAIRequest openAiRequest)
        {
            Logger.LogDebug("OpenAI request not found in session data");
            return;
        }

        AddResponseTypeSpecificTags(activity, openAiRequest, response.BodyString);

        Logger.LogTrace("ProcessSuccessResponse() finished");
    }

    private void AddResponseTypeSpecificTags(Activity activity, OpenAIRequest openAiRequest, string responseBody)
    {
        Logger.LogTrace("AddResponseTypeSpecificTags() called");

        try
        {
            switch (openAiRequest)
            {
                case OpenAIChatCompletionRequest:
                    AddChatCompletionResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAICompletionRequest:
                    AddCompletionResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIEmbeddingRequest:
                    AddEmbeddingResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIImageRequest:
                    AddImageResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIAudioRequest:
                    AddAudioResponseTags(activity, openAiRequest, responseBody);
                    break;
                case OpenAIFineTuneRequest:
                    AddFineTuneResponseTags(activity, openAiRequest, responseBody);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to deserialize OpenAI response");
            activity.SetTag("error", ex.Message);
        }
    }

    private void AddFineTuneResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddFineTuneResponseTags() called");

        var fineTuneResponse = JsonSerializer.Deserialize<OpenAIFineTuneResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (fineTuneResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, fineTuneResponse);

        activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_STATUS, fineTuneResponse.Status);
        activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, fineTuneResponse.Id);

        if (!string.IsNullOrEmpty(fineTuneResponse.FineTunedModel))
        {
            activity.SetTag("ai.response.fine_tuned_model", fineTuneResponse.FineTunedModel);
        }

        Logger.LogTrace("AddFineTuneResponseTags() finished");
    }

    private void AddAudioResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddAudioResponseTags() called");

        var audioResponse = JsonSerializer.Deserialize<OpenAIAudioTranscriptionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (audioResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, audioResponse);

        // Record the transcription text if configured
        if (_configuration.IncludeCompletion && !string.IsNullOrEmpty(audioResponse.Text))
        {
            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, audioResponse.Text);
        }

        Logger.LogTrace("AddAudioResponseTags() finished");
    }

    private void AddImageResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddImageResponseTags() called");

        var imageResponse = JsonSerializer.Deserialize<OpenAIImageResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (imageResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, imageResponse);

        activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, imageResponse.Id);

        if (imageResponse.Data != null)
        {
            activity.SetTag("ai.response.image.count", imageResponse.Data.Length);

            if (_configuration.IncludeCompletion &&
                imageResponse.Data.Length > 0 &&
                !string.IsNullOrEmpty(imageResponse.Data[0]?.RevisedPrompt))
            {
                activity.SetTag(SemanticConvention.GEN_AI_CONTENT_REVISED_PROMPT,
                    imageResponse.Data[0].RevisedPrompt);
            }
        }

        Logger.LogTrace("AddImageResponseTags() finished");
    }

    private void AddEmbeddingResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddEmbeddingResponseTags() called");

        var embeddingResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (embeddingResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, embeddingResponse);

        // Embedding response doesn't have a "completion" but we can record some metadata
        activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, embeddingResponse.Id);
        if (embeddingResponse.Data is not null)
        {
            activity.SetTag("ai.embedding.count", embeddingResponse.Data.Length);

            // If there's only one embedding, record the dimensions
            if (embeddingResponse.Data.Length == 1 &&
                embeddingResponse.Data[0]?.Embedding is not null)
            {
                activity.SetTag("ai.embedding.dimensions", embeddingResponse.Data[0]?.Embedding?.Length ?? 0);
            }
        }

        Logger.LogTrace("AddEmbeddingResponseTags() finished");
    }

    private void AddChatCompletionResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddChatCompletionResponseTags() called");

        var chatResponse = JsonSerializer.Deserialize<OpenAIChatCompletionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (chatResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, chatResponse);

        if (chatResponse.Choices?.Length > 0 && chatResponse.Choices[0] != null && chatResponse.Choices[0].Message != null)
        {
            if (_configuration.IncludeCompletion)
            {
                activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, chatResponse.Choices[0].Message.Content);
            }
            activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, chatResponse.Choices[0].FinishReason);
        }

        activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, chatResponse.Id);

        Logger.LogTrace("AddChatCompletionResponseTags() finished");
    }

    private void AddCompletionResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddCompletionResponseTags() called");

        var completionResponse = JsonSerializer.Deserialize<OpenAICompletionResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (completionResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, completionResponse);

        if (completionResponse.Choices?.Length > 0 && completionResponse.Choices[0] is not null)
        {
            if (_configuration.IncludeCompletion)
            {
                activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, completionResponse.Choices[0].Text);
            }
            activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, completionResponse.Choices[0].FinishReason);
        }

        activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, completionResponse.Id);

        Logger.LogTrace("AddCompletionResponseTags() finished");
    }

    private void AddRequestTypeSpecificTags(Activity activity, OpenAIRequest openAiRequest)
    {
        switch (openAiRequest)
        {
            case OpenAIChatCompletionRequest chatRequest:
                AddChatCompletionRequestTags(activity, chatRequest);
                break;
            case OpenAICompletionRequest completionRequest:
                AddCompletionRequestTags(activity, completionRequest);
                break;
            case OpenAIEmbeddingRequest embeddingRequest:
                AddEmbeddingRequestTags(activity, embeddingRequest);
                break;
            case OpenAIImageRequest imageRequest:
                AddImageRequestTags(activity, imageRequest);
                break;
            case OpenAIAudioRequest audioRequest:
                AddAudioRequestTags(activity, audioRequest);
                break;
            case OpenAIAudioSpeechRequest speechRequest:
                AddAudioSpeechRequestTags(activity, speechRequest);
                break;
            case OpenAIFineTuneRequest fineTuneRequest:
                AddFineTuneRequestTags(activity, fineTuneRequest);
                break;
        }
    }

    private void AddCompletionRequestTags(Activity activity, OpenAICompletionRequest completionRequest)
    {
        Logger.LogTrace("AddCompletionRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_CONTENT_COMPLETION);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_CONTENT_COMPLETION);

        if (_configuration.IncludePrompt)
        {
            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, completionRequest.Prompt);
        }

        Logger.LogTrace("AddCompletionRequestTags() finished");
    }

    private void AddChatCompletionRequestTags(Activity activity, OpenAIChatCompletionRequest chatRequest)
    {
        Logger.LogTrace("AddChatCompletionRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT);

        if (_configuration.IncludePrompt)
        {
            // Format messages to a more readable form for the span
            var formattedMessages = chatRequest.Messages
                .Select(m => $"{m.Role}: {m.Content}")
                .ToArray();

            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, string.Join("\n", formattedMessages));
        }

        Logger.LogTrace("AddChatCompletionRequestTags() finished");
    }

    private void AddEmbeddingRequestTags(Activity activity, OpenAIEmbeddingRequest embeddingRequest)
    {
        Logger.LogTrace("AddEmbeddingRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING);

        if (_configuration.IncludePrompt && embeddingRequest.Input is not null)
        {
            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, embeddingRequest.Input);
        }

        if (embeddingRequest.EncodingFormat is not null)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_ENCODING_FORMATS, embeddingRequest.EncodingFormat);
        }

        if (embeddingRequest.Dimensions.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_EMBEDDING_DIMENSION, embeddingRequest.Dimensions.Value);
        }

        Logger.LogTrace("AddEmbeddingRequestTags() finished");
    }

    private void AddImageRequestTags(Activity activity, OpenAIImageRequest imageRequest)
    {
        Logger.LogTrace("AddImageRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE);

        if (_configuration.IncludePrompt && !string.IsNullOrEmpty(imageRequest.Prompt))
        {
            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, imageRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(imageRequest.Size))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_SIZE, imageRequest.Size);
        }

        if (!string.IsNullOrEmpty(imageRequest.Quality))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_QUALITY, imageRequest.Quality);
        }

        if (!string.IsNullOrEmpty(imageRequest.Style))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_STYLE, imageRequest.Style);
        }

        if (imageRequest.N.HasValue)
        {
            activity.SetTag("ai.request.image.count", imageRequest.N.Value);
        }

        Logger.LogTrace("AddImageRequestTags() finished");
    }

    private void AddAudioRequestTags(Activity activity, OpenAIAudioRequest audioRequest)
    {
        Logger.LogTrace("AddAudioRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (!string.IsNullOrEmpty(audioRequest.ResponseFormat))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, audioRequest.ResponseFormat);
        }

        if (!string.IsNullOrEmpty(audioRequest.Prompt) && _configuration.IncludePrompt)
        {
            activity.SetTag("ai.request.audio.prompt", audioRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(audioRequest.Language))
        {
            activity.SetTag("ai.request.audio.language", audioRequest.Language);
        }

        Logger.LogTrace("AddAudioRequestTags() finished");
    }

    private void AddAudioSpeechRequestTags(Activity activity, OpenAIAudioSpeechRequest speechRequest)
    {
        Logger.LogTrace("AddAudioSpeechRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (_configuration.IncludePrompt && !string.IsNullOrEmpty(speechRequest.Input))
        {
            activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, speechRequest.Input);
        }

        if (!string.IsNullOrEmpty(speechRequest.Voice))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_VOICE, speechRequest.Voice);
        }

        if (!string.IsNullOrEmpty(speechRequest.ResponseFormat))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, speechRequest.ResponseFormat);
        }

        if (speechRequest.Speed.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_SPEED, speechRequest.Speed.Value);
        }

        Logger.LogTrace("AddAudioSpeechRequestTags() finished");
    }

    private void AddFineTuneRequestTags(Activity activity, OpenAIFineTuneRequest fineTuneRequest)
    {
        Logger.LogTrace("AddFineTuneRequestTags() called");

        // OpenLIT
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING);
        // OpenTelemetry
        activity.SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING);

        if (!string.IsNullOrEmpty(fineTuneRequest.TrainingFile))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TRAINING_FILE, fineTuneRequest.TrainingFile);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.ValidationFile))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_VALIDATION_FILE, fineTuneRequest.ValidationFile);
        }

        if (fineTuneRequest.BatchSize.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_BATCH_SIZE, fineTuneRequest.BatchSize.Value);
        }

        if (fineTuneRequest.LearningRateMultiplier.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_LRM,
                fineTuneRequest.LearningRateMultiplier.Value);
        }

        if (fineTuneRequest.Epochs.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_EPOCHS,
                fineTuneRequest.Epochs.Value);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.Suffix))
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_SUFFIX, fineTuneRequest.Suffix);
        }

        Logger.LogTrace("AddFineTuneRequestTags() finished");
    }

    private void AddCommonRequestTags(Activity activity, OpenAIRequest openAiRequest)
    {
        Logger.LogTrace("AddCommonRequestTags() called");

        if (openAiRequest.Temperature.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TEMPERATURE, openAiRequest.Temperature.Value);
        }

        if (openAiRequest.MaxTokens.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_MAX_TOKENS, openAiRequest.MaxTokens.Value);
        }

        if (openAiRequest.TopP.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TOP_P, openAiRequest.TopP.Value);
        }

        if (openAiRequest.PresencePenalty.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_PRESENCE_PENALTY, openAiRequest.PresencePenalty.Value);
        }

        if (openAiRequest.FrequencyPenalty.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FREQUENCY_PENALTY, openAiRequest.FrequencyPenalty.Value);
        }

        if (openAiRequest.Stream.HasValue)
        {
            activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IS_STREAM, openAiRequest.Stream.Value);
        }

        Logger.LogTrace("AddCommonRequestTags() finished");
    }

    private void RecordUsageMetrics(Activity activity, OpenAIRequest request, OpenAIResponse response)
    {
        Logger.LogTrace("RecordUsageMetrics() called");

        var usage = response.Usage;
        if (usage is null)
        {
            return;
        }

        Debug.Assert(_tokenUsageMetric is not null, "Token usage histogram is not initialized");
        _tokenUsageMetric.Record(response.Usage?.PromptTokens ?? 0,
        [
            new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
            new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
            new(SemanticConvention.GEN_AI_TOKEN_TYPE, SemanticConvention.GEN_AI_TOKEN_TYPE_INPUT),
            new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
            new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
        ]);
        _tokenUsageMetric.Record(response.Usage?.CompletionTokens ?? 0,
        [
            new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
            new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
            new(SemanticConvention.GEN_AI_TOKEN_TYPE, SemanticConvention.GEN_AI_TOKEN_TYPE_OUTPUT),
            new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
            new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
        ]);

        activity.SetTag(SemanticConvention.GEN_AI_USAGE_INPUT_TOKENS, usage.PromptTokens);
        activity.SetTag(SemanticConvention.GEN_AI_USAGE_OUTPUT_TOKENS, usage.CompletionTokens);
        activity.SetTag(SemanticConvention.GEN_AI_USAGE_TOTAL_TOKENS, usage.TotalTokens);

        if (!_configuration.IncludeCosts || _configuration.Prices is null)
        {
            Logger.LogDebug("Cost tracking is disabled or prices data is not available");
            return;
        }

        if (string.IsNullOrEmpty(response.Model))
        {
            Logger.LogDebug("Response model is empty or null");
            return;
        }

        var (inputCost, outputCost) = _configuration.Prices.CalculateCost(response.Model, usage.PromptTokens, usage.CompletionTokens);

        if (inputCost > 0)
        {
            var totalCost = inputCost + outputCost;
            activity.SetTag(SemanticConvention.GEN_AI_USAGE_COST, totalCost);

            Debug.Assert(_requestCostMetric is not null, "Cost histogram is not initialized");
            Debug.Assert(_totalCostMetric is not null, "Total cost counter is not initialized");

            _requestCostMetric.Record(totalCost,
            [
                new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
                new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
                new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
                new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
            ]);
            _totalCostMetric.Add(totalCost,
            [
                new(SemanticConvention.GEN_AI_OPERATION_NAME, GetOperationName(request)),
                new(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem),
                new(SemanticConvention.GEN_AI_REQUEST_MODEL, request.Model),
                new(SemanticConvention.GEN_AI_RESPONSE_MODEL, response.Model)
            ]);
        }
        else
        {
            Logger.LogDebug("Input cost is zero, skipping cost metrics recording");
        }

        Logger.LogTrace("RecordUsageMetrics() finished");
    }

    private static string GetOperationName(OpenAIRequest request)
    {
        if (request == null)
        {
            return "unknown";
        }

        return request switch
        {
            OpenAIChatCompletionRequest => "chat.completions",
            OpenAICompletionRequest => "completions",
            OpenAIEmbeddingRequest => "embeddings",
            OpenAIImageRequest => "images.generations",
            OpenAIAudioRequest => "audio.transcriptions",
            OpenAIAudioSpeechRequest => "audio.speech",
            OpenAIFineTuneRequest => "fine_tuning.jobs",
            _ => "unknown"
        };
    }

    private bool TryGetOpenAIRequest(string content, out OpenAIRequest? request)
    {
        Logger.LogTrace("TryGetOpenAIRequest() called");

        request = null;

        if (string.IsNullOrEmpty(content))
        {
            Logger.LogDebug("Request content is empty or null");
            return false;
        }

        try
        {
            Logger.LogDebug("Checking if the request is an OpenAI request...");

            var rawRequest = JsonSerializer.Deserialize<JsonElement>(content, ProxyUtils.JsonSerializerOptions);

            // Check for completion request (has "prompt", but not specific to image)
            if (rawRequest.TryGetProperty("prompt", out _) &&
                !rawRequest.TryGetProperty("size", out _) &&
                !rawRequest.TryGetProperty("n", out _))
            {
                Logger.LogDebug("Request is a completion request");
                request = JsonSerializer.Deserialize<OpenAICompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Chat completion request
            if (rawRequest.TryGetProperty("messages", out _))
            {
                Logger.LogDebug("Request is a chat completion request");
                request = JsonSerializer.Deserialize<OpenAIChatCompletionRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Embedding request
            if (rawRequest.TryGetProperty("input", out _) &&
                rawRequest.TryGetProperty("model", out _) &&
                !rawRequest.TryGetProperty("voice", out _))
            {
                Logger.LogDebug("Request is an embedding request");
                request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Image generation request
            if (rawRequest.TryGetProperty("prompt", out _) &&
                (rawRequest.TryGetProperty("size", out _) || rawRequest.TryGetProperty("n", out _)))
            {
                Logger.LogDebug("Request is an image generation request");
                request = JsonSerializer.Deserialize<OpenAIImageRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio transcription request
            if (rawRequest.TryGetProperty("file", out _))
            {
                Logger.LogDebug("Request is an audio transcription request");
                request = JsonSerializer.Deserialize<OpenAIAudioRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Audio speech synthesis request
            if (rawRequest.TryGetProperty("input", out _) && rawRequest.TryGetProperty("voice", out _))
            {
                Logger.LogDebug("Request is an audio speech synthesis request");
                request = JsonSerializer.Deserialize<OpenAIAudioSpeechRequest>(content, ProxyUtils.JsonSerializerOptions);
                return true;
            }

            // Fine-tuning request
            if (rawRequest.TryGetProperty("training_file", out _))
            {
                Logger.LogDebug("Request is a fine-tuning request");
                request = JsonSerializer.Deserialize<OpenAIFineTuneRequest>(content, ProxyUtils.JsonSerializerOptions);
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

    public void Dispose()
    {
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
    }
}
