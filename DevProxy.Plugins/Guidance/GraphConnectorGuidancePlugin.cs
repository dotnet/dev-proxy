// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Guidance;

sealed class ExternalConnectionSchema
{
    public string? BaseType { get; set; }
    public ExternalConnectionSchemaProperty[]? Properties { get; set; }
}

sealed class ExternalConnectionSchemaProperty
{
    public string[]? Aliases { get; set; }
    public bool? IsQueryable { get; set; }
    public bool? IsRefinable { get; set; }
    public bool? IsRetrievable { get; set; }
    public bool? IsSearchable { get; set; }
    public string[]? Labels { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
}

public sealed class GraphConnectorGuidancePlugin(
    ILogger<GraphConnectorGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphConnectorGuidancePlugin);

    public override Func<RequestArguments, CancellationToken, Task>? ProvideRequestGuidanceAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(ProvideRequestGuidanceAsync));

        ArgumentNullException.ThrowIfNull(args);

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return;
        }
        if (args.Request.Method != HttpMethod.Patch)
        {
            Logger.LogRequest("Skipping non-PATCH request", MessageType.Skipped, args.Request);
            return;
        }

        try
        {
            var schemaString = string.Empty;
            if (args.Request.Content is not null)
            {
                schemaString = await args.Request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (string.IsNullOrEmpty(schemaString))
            {
                Logger.LogRequest("No schema found in the request body.", MessageType.Failed, args.Request);
                return;
            }

            var schema = JsonSerializer.Deserialize<ExternalConnectionSchema>(schemaString, ProxyUtils.JsonSerializerOptions);
            if (schema is null || schema.Properties is null)
            {
                Logger.LogRequest("Invalid schema found in the request body.", MessageType.Failed, args.Request);
                return;
            }

            bool hasTitle = false, hasIconUrl = false, hasUrl = false;
            foreach (var property in schema.Properties)
            {
                if (property.Labels is null)
                {
                    continue;
                }

                if (property.Labels.Contains("title", StringComparer.OrdinalIgnoreCase))
                {
                    hasTitle = true;
                }
                if (property.Labels.Contains("iconUrl", StringComparer.OrdinalIgnoreCase))
                {
                    hasIconUrl = true;
                }
                if (property.Labels.Contains("url", StringComparer.OrdinalIgnoreCase))
                {
                    hasUrl = true;
                }
            }

            if (!hasTitle || !hasIconUrl || !hasUrl)
            {
                string[] missingLabels = [
                    !hasTitle ? "title" : "",
                    !hasIconUrl ? "iconUrl" : "",
                    !hasUrl ? "url" : ""
                ];

                Logger.LogRequest(
                    $"The schema is missing the following semantic labels: {string.Join(", ", missingLabels.Where(s => !string.IsNullOrEmpty(s)))}. Ingested content might not show up in Microsoft Copilot for Microsoft 365. More information: https://aka.ms/devproxy/guidance/gc/ux",
                    MessageType.Failed, args.Request
                );
            }
            else
            {
                Logger.LogRequest("The schema contains all the required semantic labels.", MessageType.Skipped, args.Request);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while deserializing the request body");
        }

        Logger.LogTrace("Left {Name}", nameof(ProvideRequestGuidanceAsync));
    };
}
