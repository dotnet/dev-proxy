// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Guidance;

class ExternalConnectionSchema
{
    public string? BaseType { get; set; }
    public ExternalConnectionSchemaProperty[]? Properties { get; set; }
}

class ExternalConnectionSchemaProperty
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

public class GraphConnectorGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(GraphConnectorGuidancePlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.BeforeRequest += BeforeRequestAsync;
    }

    private Task BeforeRequestAsync(object sender, ProxyRequestArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (!string.Equals(e.Session.HttpClient.Request.Method, "PATCH", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-PATCH request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        try
        {
            var schemaString = e.Session.HttpClient.Request.BodyString;
            if (string.IsNullOrEmpty(schemaString))
            {
                Logger.LogRequest("No schema found in the request body.", MessageType.Failed, new LoggingContext(e.Session));
                return Task.CompletedTask;
            }

            var schema = JsonSerializer.Deserialize<ExternalConnectionSchema>(schemaString, ProxyUtils.JsonSerializerOptions);
            if (schema is null || schema.Properties is null)
            {
                Logger.LogRequest("Invalid schema found in the request body.", MessageType.Failed, new LoggingContext(e.Session));
                return Task.CompletedTask;
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
                    $"The schema is missing the following semantic labels: {string.Join(", ", missingLabels.Where(s => s != ""))}. Ingested content might not show up in Microsoft Copilot for Microsoft 365. More information: https://aka.ms/devproxy/guidance/gc/ux",
                    MessageType.Failed, new LoggingContext(e.Session)
                );
            }
            else
            {
                Logger.LogRequest("The schema contains all the required semantic labels.", MessageType.Skipped, new LoggingContext(e.Session));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error has occurred while deserializing the request body");
        }

        return Task.CompletedTask;
    }
}
