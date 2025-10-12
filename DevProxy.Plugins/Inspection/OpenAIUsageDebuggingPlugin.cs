// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.LanguageModel;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace DevProxy.Plugins.Inspection;

sealed class UsageRecord
{
    public DateTime Time { get; set; }
    public int Status { get; set; }
    public string? RetryAfter { get; set; }
    public string? Policy { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public long? CachedTokens { get; set; }
    public long? TotalTokens { get; set; }
    public long? RemainingTokens { get; set; }
    public long? RemainingRequests { get; set; }

    public override string ToString()
    {
        return $"{Time:O},{Status},{RetryAfter},{Policy},{PromptTokens},{CompletionTokens},{CachedTokens},{TotalTokens},{RemainingTokens},{RemainingRequests}";
    }

    public static string GetHeader()
    {
        return "time,status,retry-after,policy,prompt tokens,completion tokens,cached tokens,total tokens,remaining tokens,remaining requests";
    }
}

public sealed class OpenAIUsageDebuggingPlugin(
    ILogger<OpenAIUsageDebuggingPlugin> logger,
    ISet<UrlToWatch> urlsToWatch) :
    BaseReportingPlugin(
        logger,
        urlsToWatch)
{
    public override string Name => nameof(OpenAIUsageDebuggingPlugin);

    private readonly string outputFileName = $"devproxy_llmusage_{DateTime.Now:yyyyMMddHHmmss}.csv";

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(InitializeAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!File.Exists(outputFileName))
        {
            await File.AppendAllLinesAsync(outputFileName, [UsageRecord.GetHeader()], cancellationToken);
        }

        Logger.LogTrace("{Method} finished", nameof(InitializeAsync));
    }

    public override async Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
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

        if (!OpenAIRequest.TryGetOpenAIRequest(request.BodyString, Logger, out var openAiRequest) || openAiRequest is null)
        {
            Logger.LogRequest("Skipping non-OpenAI request", MessageType.Skipped, new(e.Session));
            return;
        }

        var response = e.Session.HttpClient.Response;
        var bodyString = response.BodyString;
        if (HttpUtils.IsStreamingResponse(response, Logger))
        {
            bodyString = HttpUtils.GetBodyFromStreamingResponse(response, Logger);
        }

        var usage = new UsageRecord
        {
            Time = DateTime.Parse(e.Session.HttpClient.Response.Headers.FirstOrDefault(h => h.Name.Equals("date", StringComparison.OrdinalIgnoreCase))?.Value ?? DateTime.Now.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            Status = e.Session.HttpClient.Response.StatusCode
        };

#pragma warning disable IDE0010
        switch (response.StatusCode)
#pragma warning restore IDE0010
        {
            case int code when code is >= 200 and < 300:
                ProcessSuccessResponse(bodyString, usage, e);
                break;
            case int code when code >= 400:
                ProcessErrorResponse(usage, e);
                break;
        }

        await File.AppendAllLinesAsync(outputFileName, [usage.ToString()], cancellationToken);
        Logger.LogRequest("Processed OpenAI request", MessageType.Processed, new(e.Session));

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
    }

    private void ProcessSuccessResponse(string responseBody, UsageRecord usage, ProxyResponseArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(ProcessSuccessResponse));

        var oaiResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseBody, ProxyUtils.JsonSerializerOptions);
        if (oaiResponse is null)
        {
            return;
        }

        var response = e.Session.HttpClient.Response;

        usage.PromptTokens = oaiResponse.Usage?.PromptTokens;
        usage.CompletionTokens = oaiResponse.Usage?.CompletionTokens;
        usage.CachedTokens = oaiResponse.Usage?.PromptTokensDetails?.CachedTokens;
        usage.TotalTokens = oaiResponse.Usage?.TotalTokens;
        usage.RemainingTokens = response.Headers.FirstOrDefault(h => h.Name.Equals("x-ratelimit-remaining-tokens", StringComparison.OrdinalIgnoreCase))?.Value is string remainingTokensStr && long.TryParse(remainingTokensStr, out var remainingTokens) ? remainingTokens : null;
        usage.RemainingRequests = response.Headers.FirstOrDefault(h => h.Name.Equals("x-ratelimit-remaining-requests", StringComparison.OrdinalIgnoreCase))?.Value is string remainingRequestsStr && long.TryParse(remainingRequestsStr, out var remainingRequests) ? remainingRequests : null;

        Logger.LogTrace("Left {Name}", nameof(ProcessSuccessResponse));
    }
    private void ProcessErrorResponse(UsageRecord usage, ProxyResponseArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(ProcessErrorResponse));

        var response = e.Session.HttpClient.Response;

        usage.RetryAfter = response.Headers.FirstOrDefault(h => h.Name.Equals("retry-after", StringComparison.OrdinalIgnoreCase))?.Value;
        usage.Policy = response.Headers.FirstOrDefault(h => h.Name.Equals("policy-id", StringComparison.OrdinalIgnoreCase))?.Value;

        Logger.LogTrace("Left {Name}", nameof(ProcessErrorResponse));
    }
}