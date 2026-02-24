// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.OpenTelemetry;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

public class LanguageModelPricesPluginConfiguration
{
#pragma warning disable CA2227
    public PricesData? Prices { get; set; }
#pragma warning restore CA2227
    public string? PricesFile { get; set; }
}

public sealed class OpenAITelemetryPluginConfiguration : LanguageModelPricesPluginConfiguration
{
    public string Application { get; set; } = "default";
    public string Currency { get; set; } = "USD";
    public string Environment { get; set; } = "development";
    public string ExporterEndpoint { get; set; } = "http://localhost:4318";
    public bool IncludeCompletion { get; set; } = true;
    public bool IncludeCosts { get; set; }
    public bool IncludePrompt { get; set; } = true;
}

public sealed class OpenAITelemetryPlugin(
    HttpClient httpClient,
    ILogger<OpenAITelemetryPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<OpenAITelemetryPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string ActivitySourceName = "DevProxy.OpenAI";
    private const string OpenAISystem = "openai";

    private static readonly Meter _meter = new(ActivitySourceName);
    private static Histogram<double>? _requestCostMetric;
    private static Counter<double>? _totalCostMetric;
    private static Histogram<long>? _tokenUsageMetric;

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private LanguageModelPricesLoader? _loader;
    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;

    public override string Name => nameof(OpenAITelemetryPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        await base.InitializeAsync(e, cancellationToken);

        if (Configuration.IncludeCosts)
        {
            Configuration.PricesFile = ProxyUtils.GetFullPath(Configuration.PricesFile, ProxyConfiguration.ConfigFile);
            _loader = ActivatorUtilities.CreateInstance<LanguageModelPricesLoader>(e.ServiceProvider, Configuration);
            await _loader.InitFileWatcherAsync(cancellationToken);
        }

        InitializeOpenTelemetryExporter();
    }

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        var request = e.Session.HttpClient.Request;
        if (request.Method is null ||
            !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !request.HasBody)
        {
            Logger.LogRequest("Request is not a POST request with a body", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (!OpenAIRequest.TryGetOpenAIRequest(request.BodyString, Logger, out var openAiRequest) || openAiRequest is null)
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        // store for use in response
        e.SessionData["OpenAIRequest"] = openAiRequest;

        var activity = _activitySource.StartActivity(
            $"openai.{GetOperationName(openAiRequest)}",
            ActivityKind.Client);

        if (activity is null)
        {
            Logger.LogWarning("Failed to start OpenTelemetry activity for OpenAI request");
            return Task.CompletedTask;
        }

        // add generic request tags
        _ = activity.SetTag("http.method", request.Method)
            .SetTag("http.url", request.RequestUri.ToString())
            .SetTag("http.scheme", request.RequestUri.Scheme)
            .SetTag("http.host", request.RequestUri.Host)
            .SetTag("http.target", request.RequestUri.PathAndQuery)
            .SetTag(SemanticConvention.GEN_AI_SYSTEM, OpenAISystem)
            .SetTag(SemanticConvention.GEN_AI_ENVIRONMENT, Configuration.Environment)
            .SetTag(SemanticConvention.GEN_AI_APPLICATION_NAME, Configuration.Application);

        AddCommonRequestTags(activity, openAiRequest);
        AddRequestTypeSpecificTags(activity, openAiRequest);

        // store for use in response
        e.SessionData["OpenAIActivity"] = activity;

        Logger.LogTrace("OnRequestAsync() finished");
        return Task.CompletedTask;
    }

    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.SessionData.TryGetValue("OpenAIActivity", out var activityObj) ||
            activityObj is not Activity activity)
        {
            return Task.CompletedTask;
        }

        try
        {
            var response = e.Session.HttpClient.Response;

            _ = activity.SetTag("http.status_code", response.StatusCode);

#pragma warning disable IDE0010
            switch (response.StatusCode)
#pragma warning restore IDE0010
            {
                case int code when code is >= 200 and < 300:
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
            _ = e.SessionData.Remove("OpenAIActivity");
            _ = e.SessionData.Remove("OpenAIRequest");

            Logger.LogRequest("OpenTelemetry information emitted", MessageType.Processed, new LoggingContext(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    public override Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        var report = new OpenAITelemetryPluginReport
        {
            Application = Configuration.Application,
            Environment = Configuration.Environment,
            Currency = Configuration.Currency,
            IncludeCosts = Configuration.IncludeCosts,
            ModelUsage = GetOpenAIModelUsage(e.RequestLogs)
        };

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
        return Task.CompletedTask;
    }

    private void InitializeOpenTelemetryExporter()
    {
        Logger.LogTrace("InitializeOpenTelemetryExporter() called");

        try
        {
            var baseExporterUri = new Uri(Configuration.ExporterEndpoint);

            void configureTracesOtlpExporter(OtlpExporterOptions options)
            {
                // We use protobuf to allow intercepting Dev Proxy's own LLM traffic
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri(baseExporterUri, "/v1/traces");
            }

            void configureMetricsOtlpExporter(OtlpExporterOptions options)
            {
                // We use protobuf to allow intercepting Dev Proxy's own LLM traffic
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri(baseExporterUri, "/v1/metrics");
            }

            var resourceBuilder = ResourceBuilder
                .CreateDefault()
                .AddService(serviceName: "DevProxy.OpenAI", serviceVersion: ProxyUtils.ProductVersion);

            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(ActivitySourceName)
                .AddOtlpExporter(configureTracesOtlpExporter)
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
                .AddOtlpExporter(configureMetricsOtlpExporter)
                .Build();

            _tokenUsageMetric = _meter.CreateHistogram<long>(
                SemanticConvention.GEN_AI_METRIC_CLIENT_TOKEN_USAGE,
                "tokens",
                "Number of tokens processed");
            _requestCostMetric = _meter.CreateHistogram<double>(
                SemanticConvention.GEN_AI_USAGE_COST,
                "cost",
                $"Estimated cost per request in {Configuration.Currency}");
            _totalCostMetric = _meter.CreateCounter<double>(
                SemanticConvention.GEN_AI_USAGE_TOTAL_COST,
                "cost",
                $"Total estimated cost for the session in {Configuration.Currency}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize OpenTelemetry exporter");
        }

        Logger.LogTrace("InitializeOpenTelemetryExporter() finished");
    }

    private void ProcessErrorResponse(Activity activity, ProxyResponseArgs e)
    {
        Logger.LogTrace("ProcessErrorResponse() called");

        var response = e.Session.HttpClient.Response;

        _ = activity.SetTag("error", true)
            .SetTag("error.type", "http")
            .SetTag("error.message", $"HTTP {response.StatusCode}");

        if (response.HasBody)
        {
            try
            {
                var errorObj = JsonSerializer.Deserialize<JsonElement>(response.BodyString);
                if (errorObj.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("message", out var message))
                    {
                        _ = activity.SetTag("error.details", message.GetString());
                    }
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

        var bodyString = response.BodyString;
        if (HttpUtils.IsStreamingResponse(response, Logger))
        {
            bodyString = HttpUtils.GetBodyFromStreamingResponse(response, Logger);
        }

        AddResponseTypeSpecificTags(activity, openAiRequest, bodyString);

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
                case OpenAIResponsesRequest:
                    AddResponsesResponseTags(activity, openAiRequest, responseBody);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported OpenAI request type: {openAiRequest.GetType().Name}");
            }
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to deserialize OpenAI response");
            _ = activity.SetTag("error", ex.Message);
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

        _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_STATUS, fineTuneResponse.Status)
            .SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, fineTuneResponse.Id);

        if (!string.IsNullOrEmpty(fineTuneResponse.FineTunedModel))
        {
            _ = activity.SetTag("ai.response.fine_tuned_model", fineTuneResponse.FineTunedModel);
        }

        Logger.LogTrace("AddFineTuneResponseTags() finished");
    }

    private void AddResponsesResponseTags(Activity activity, OpenAIRequest openAIRequest, string responseBody)
    {
        Logger.LogTrace("AddResponsesResponseTags() called");

        var responsesResponse = JsonSerializer.Deserialize<OpenAIResponsesResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (responsesResponse is null)
        {
            return;
        }

        RecordUsageMetrics(activity, openAIRequest, responsesResponse);

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, responsesResponse.Id);

        if (!string.IsNullOrEmpty(responsesResponse.Status))
        {
            _ = activity.SetTag("ai.response.status", responsesResponse.Status);
        }

        if (Configuration.IncludeCompletion && responsesResponse.Response is not null)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, responsesResponse.Response);
        }

        Logger.LogTrace("AddResponsesResponseTags() finished");
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
        if (Configuration.IncludeCompletion && !string.IsNullOrEmpty(audioResponse.Text))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, audioResponse.Text);
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

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, imageResponse.Id);

        if (imageResponse.Data != null)
        {
            _ = activity.SetTag("ai.response.image.count", imageResponse.Data.Count());

            if (Configuration.IncludeCompletion &&
                imageResponse.Data.Any() &&
                !string.IsNullOrEmpty(imageResponse.Data.FirstOrDefault()?.RevisedPrompt))
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_REVISED_PROMPT,
                    imageResponse.Data.First().RevisedPrompt);
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
        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, embeddingResponse.Id);

        if (embeddingResponse.Data is not null)
        {
            _ = activity.SetTag("ai.embedding.count", embeddingResponse.Data.Count());

            // If there's only one embedding, record the dimensions
            if (embeddingResponse.Data.Count() == 1 &&
                embeddingResponse.Data.First().Embedding is not null)
            {
                _ = activity.SetTag("ai.embedding.dimensions", embeddingResponse.Data.FirstOrDefault()?.Embedding?.Count() ?? 0);
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

        if (chatResponse.Choices?.FirstOrDefault()?.Message is not null)
        {
            if (Configuration.IncludeCompletion)
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, chatResponse.Choices.First().Message.Content);
            }

            _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, chatResponse.Choices.First().FinishReason);
        }

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, chatResponse.Id);

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

        if (completionResponse.Choices?.FirstOrDefault() is not null)
        {
            if (Configuration.IncludeCompletion)
            {
                _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_COMPLETION, completionResponse.Choices.First().Text);
            }

            _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_FINISH_REASON, completionResponse.Choices.First().FinishReason);
        }

        _ = activity.SetTag(SemanticConvention.GEN_AI_RESPONSE_ID, completionResponse.Id);

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
            case OpenAIResponsesRequest responsesRequest:
                AddResponsesRequestTags(activity, responsesRequest);
                break;
            default:
                throw new InvalidOperationException($"Unsupported OpenAI request type: {openAiRequest.GetType().Name}");
        }
    }

    private void AddCompletionRequestTags(Activity activity, OpenAICompletionRequest completionRequest)
    {
        Logger.LogTrace("AddCompletionRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_CONTENT_COMPLETION)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_CONTENT_COMPLETION);

        if (Configuration.IncludePrompt)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, completionRequest.Prompt);
        }

        Logger.LogTrace("AddCompletionRequestTags() finished");
    }

    private void AddChatCompletionRequestTags(Activity activity, OpenAIChatCompletionRequest chatRequest)
    {
        Logger.LogTrace("AddChatCompletionRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT);

        if (Configuration.IncludePrompt)
        {
            // Format messages to a more readable form for the span
            var formattedMessages = chatRequest.Messages
                .Select(m => $"{m.Role}: {m.Content}")
                .ToArray();

            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, string.Join("\n", formattedMessages));
        }

        Logger.LogTrace("AddChatCompletionRequestTags() finished");
    }

    private void AddEmbeddingRequestTags(Activity activity, OpenAIEmbeddingRequest embeddingRequest)
    {
        Logger.LogTrace("AddEmbeddingRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_EMBEDDING);

        if (Configuration.IncludePrompt && embeddingRequest.Input is not null)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, embeddingRequest.Input);
        }

        if (embeddingRequest.EncodingFormat is not null)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_ENCODING_FORMATS, embeddingRequest.EncodingFormat);
        }

        if (embeddingRequest.Dimensions.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_EMBEDDING_DIMENSION, embeddingRequest.Dimensions.Value);
        }

        Logger.LogTrace("AddEmbeddingRequestTags() finished");
    }

    private void AddImageRequestTags(Activity activity, OpenAIImageRequest imageRequest)
    {
        Logger.LogTrace("AddImageRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_IMAGE);

        if (Configuration.IncludePrompt && !string.IsNullOrEmpty(imageRequest.Prompt))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, imageRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(imageRequest.Size))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_SIZE, imageRequest.Size);
        }

        if (!string.IsNullOrEmpty(imageRequest.Quality))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_QUALITY, imageRequest.Quality);
        }

        if (!string.IsNullOrEmpty(imageRequest.Style))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IMAGE_STYLE, imageRequest.Style);
        }

        if (imageRequest.N.HasValue)
        {
            _ = activity.SetTag("ai.request.image.count", imageRequest.N.Value);
        }

        Logger.LogTrace("AddImageRequestTags() finished");
    }

    private void AddAudioRequestTags(Activity activity, OpenAIAudioRequest audioRequest)
    {
        Logger.LogTrace("AddAudioRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (!string.IsNullOrEmpty(audioRequest.ResponseFormat))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, audioRequest.ResponseFormat);
        }

        if (!string.IsNullOrEmpty(audioRequest.Prompt) && Configuration.IncludePrompt)
        {
            _ = activity.SetTag("ai.request.audio.prompt", audioRequest.Prompt);
        }

        if (!string.IsNullOrEmpty(audioRequest.Language))
        {
            _ = activity.SetTag("ai.request.audio.language", audioRequest.Language);
        }

        Logger.LogTrace("AddAudioRequestTags() finished");
    }

    private void AddAudioSpeechRequestTags(Activity activity, OpenAIAudioSpeechRequest speechRequest)
    {
        Logger.LogTrace("AddAudioSpeechRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_AUDIO);

        if (Configuration.IncludePrompt && !string.IsNullOrEmpty(speechRequest.Input))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, speechRequest.Input);
        }

        if (!string.IsNullOrEmpty(speechRequest.Voice))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_VOICE, speechRequest.Voice);
        }

        if (!string.IsNullOrEmpty(speechRequest.ResponseFormat))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_RESPONSE_FORMAT, speechRequest.ResponseFormat);
        }

        if (speechRequest.Speed.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_AUDIO_SPEED, speechRequest.Speed.Value);
        }

        Logger.LogTrace("AddAudioSpeechRequestTags() finished");
    }

    private void AddFineTuneRequestTags(Activity activity, OpenAIFineTuneRequest fineTuneRequest)
    {
        Logger.LogTrace("AddFineTuneRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, SemanticConvention.GEN_AI_OPERATION_TYPE_FINETUNING);

        if (!string.IsNullOrEmpty(fineTuneRequest.TrainingFile))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TRAINING_FILE, fineTuneRequest.TrainingFile);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.ValidationFile))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_VALIDATION_FILE, fineTuneRequest.ValidationFile);
        }

        if (fineTuneRequest.BatchSize.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_BATCH_SIZE, fineTuneRequest.BatchSize.Value);
        }

        if (fineTuneRequest.LearningRateMultiplier.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_LRM,
                fineTuneRequest.LearningRateMultiplier.Value);
        }

        if (fineTuneRequest.Epochs.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_EPOCHS,
                fineTuneRequest.Epochs.Value);
        }

        if (!string.IsNullOrEmpty(fineTuneRequest.Suffix))
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FINETUNE_MODEL_SUFFIX,
                fineTuneRequest.Suffix);
        }

        Logger.LogTrace("AddFineTuneRequestTags() finished");
    }

    private void AddResponsesRequestTags(Activity activity, OpenAIResponsesRequest responsesRequest)
    {
        Logger.LogTrace("AddResponsesRequestTags() called");

        // OpenLIT
        _ = activity.SetTag(SemanticConvention.GEN_AI_OPERATION, SemanticConvention.GEN_AI_OPERATION_TYPE_CHAT)
        // OpenTelemetry
            .SetTag(SemanticConvention.GEN_AI_OPERATION_NAME, "responses");

        if (Configuration.IncludePrompt && responsesRequest.Input is not null)
        {
            // Format input items to a more readable form for the span
            var formattedInputs = responsesRequest.Input
                .Select(i => $"{i.Role}: {(i.Content is string s ? s : JsonSerializer.Serialize(i.Content, ProxyUtils.JsonSerializerOptions))}")
                .ToArray();

            _ = activity.SetTag(SemanticConvention.GEN_AI_CONTENT_PROMPT, string.Join("\n", formattedInputs));
        }

        if (!string.IsNullOrEmpty(responsesRequest.Instructions))
        {
            _ = activity.SetTag("ai.request.instructions", responsesRequest.Instructions);
        }

        if (!string.IsNullOrEmpty(responsesRequest.PreviousResponseId))
        {
            _ = activity.SetTag("ai.request.previous_response_id", responsesRequest.PreviousResponseId);
        }

        if (responsesRequest.MaxOutputTokens.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_MAX_TOKENS, responsesRequest.MaxOutputTokens.Value);
        }

        Logger.LogTrace("AddResponsesRequestTags() finished");
    }

    private void AddCommonRequestTags(Activity activity, OpenAIRequest openAiRequest)
    {
        Logger.LogTrace("AddCommonRequestTags() called");

        if (openAiRequest.Temperature.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TEMPERATURE,
                openAiRequest.Temperature.Value);
        }

        if (openAiRequest.MaxTokens.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_MAX_TOKENS,
                openAiRequest.MaxTokens.Value);
        }

        if (openAiRequest.TopP.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_TOP_P,
                openAiRequest.TopP.Value);
        }

        if (openAiRequest.PresencePenalty.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_PRESENCE_PENALTY,
                openAiRequest.PresencePenalty.Value);
        }

        if (openAiRequest.FrequencyPenalty.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_FREQUENCY_PENALTY,
                openAiRequest.FrequencyPenalty.Value);
        }

        if (openAiRequest.Stream.HasValue)
        {
            _ = activity.SetTag(SemanticConvention.GEN_AI_REQUEST_IS_STREAM,
                openAiRequest.Stream.Value);
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

        _ = activity.SetTag(SemanticConvention.GEN_AI_USAGE_INPUT_TOKENS, usage.PromptTokens)
            .SetTag(SemanticConvention.GEN_AI_USAGE_OUTPUT_TOKENS, usage.CompletionTokens)
            .SetTag(SemanticConvention.GEN_AI_USAGE_TOTAL_TOKENS, usage.TotalTokens);

        if (!Configuration.IncludeCosts || Configuration.Prices is null)
        {
            Logger.LogDebug("Cost tracking is disabled or prices data is not available");
            return;
        }

        if (string.IsNullOrEmpty(response.Model))
        {
            Logger.LogDebug("Response model is empty or null");
            return;
        }

        var (inputCost, outputCost) = Configuration.Prices.CalculateCost(response.Model, usage.PromptTokens, usage.CompletionTokens);

        if (inputCost > 0)
        {
            var totalCost = inputCost + outputCost;
            _ = activity.SetTag(SemanticConvention.GEN_AI_USAGE_COST, totalCost);

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

    private Dictionary<string, List<OpenAITelemetryPluginReportModelUsageInformation>> GetOpenAIModelUsage(IEnumerable<RequestLog> requestLogs)
    {
        var modelUsage = new Dictionary<string, List<OpenAITelemetryPluginReportModelUsageInformation>>();
        var openAIRequestLogs = requestLogs.Where(r =>
            r is not null &&
            r.Context is not null &&
            r.Context.Session is not null &&
            r.MessageType == MessageType.InterceptedResponse &&
            string.Equals("POST", r.Context.Session.HttpClient.Request.Method, StringComparison.OrdinalIgnoreCase) &&
            r.Context.Session.HttpClient.Response.StatusCode >= 200 &&
            r.Context.Session.HttpClient.Response.StatusCode < 300 &&
            r.Context.Session.HttpClient.Response.HasBody &&
            !string.IsNullOrEmpty(r.Context.Session.HttpClient.Response.BodyString) &&
            ProxyUtils.MatchesUrlToWatch(UrlsToWatch, r.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri) &&
            OpenAIRequest.TryGetOpenAIRequest(r.Context.Session.HttpClient.Request.BodyString, NullLogger.Instance, out var openAiRequest) &&
            openAiRequest is not null
        );

        foreach (var requestLog in openAIRequestLogs)
        {
            try
            {
                var response = JsonSerializer.Deserialize<OpenAIResponse>(requestLog.Context!.Session.HttpClient.Response.BodyString, ProxyUtils.JsonSerializerOptions);
                if (response is null)
                {
                    continue;
                }

                var reportModelUsageInfo = GetReportModelUsageInfo(response);
                if (modelUsage.TryGetValue(response.Model, out var usagePerModel))
                {
                    usagePerModel.AddRange(reportModelUsageInfo);
                }
                else
                {
                    modelUsage.Add(response.Model, reportModelUsageInfo);
                }
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Failed to deserialize OpenAI response");
            }
        }

        return modelUsage;
    }

    private List<OpenAITelemetryPluginReportModelUsageInformation> GetReportModelUsageInfo(OpenAIResponse response)
    {
        Logger.LogTrace("GetReportModelUsageInfo() called");
        var usagePerModel = new List<OpenAITelemetryPluginReportModelUsageInformation>();
        var usage = response.Usage;
        if (usage is null)
        {
            return usagePerModel;
        }

        var reportModelUsageInformation = new OpenAITelemetryPluginReportModelUsageInformation
        {
            Model = response.Model,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            CachedTokens = usage.PromptTokensDetails?.CachedTokens ?? 0L
        };
        usagePerModel.Add(reportModelUsageInformation);

        if (!Configuration.IncludeCosts || Configuration.Prices is null)
        {
            Logger.LogDebug("Cost tracking is disabled or prices data is not available");
            return usagePerModel;
        }

        if (string.IsNullOrEmpty(response.Model))
        {
            Logger.LogDebug("Response model is empty or null");
            return usagePerModel;
        }

        var (inputCost, outputCost) = Configuration.Prices.CalculateCost(response.Model, usage.PromptTokens, usage.CompletionTokens);

        if (inputCost > 0)
        {
            var totalCost = inputCost + outputCost;
            reportModelUsageInformation.Cost = totalCost;
        }
        else
        {
            Logger.LogDebug("Input cost is zero, skipping cost metrics recording");
        }

        Logger.LogTrace("GetReportModelUsageInfo() finished");
        return usagePerModel;
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
            OpenAIResponsesRequest => "responses",
            _ => "unknown"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loader?.Dispose();
            _activitySource?.Dispose();
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
        }
        base.Dispose(disposing);
    }
}
