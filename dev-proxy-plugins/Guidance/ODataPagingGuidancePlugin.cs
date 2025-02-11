// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class ODataPagingGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(ODataPagingGuidancePlugin);
    private readonly IList<string> pagingUrls = [];

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.BeforeRequest += OnBeforeRequestAsync;
        PluginEvents.BeforeResponse += OnBeforeResponseAsync;
    }

    private Task OnBeforeRequestAsync(object? sender, ProxyRequestArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }
        if (!string.Equals(e.Session.HttpClient.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (IsODataPagingUrl(e.Session.HttpClient.Request.RequestUri))
        {
            if (!pagingUrls.Contains(e.Session.HttpClient.Request.Url))
            {
                Logger.LogRequest(BuildIncorrectPagingUrlMessage(), MessageType.Warning, new LoggingContext(e.Session));
            }
            else
            {
                Logger.LogRequest("Paging URL is correct", MessageType.Skipped, new LoggingContext(e.Session));
            }
        }
        else
        {
            Logger.LogRequest("Not an OData paging URL", MessageType.Skipped, new LoggingContext(e.Session));
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforeResponseAsync(object? sender, ProxyResponseArgs e)
    {
        if (UrlsToWatch is null ||
            !e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (!string.Equals(e.Session.HttpClient.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (e.Session.HttpClient.Response.StatusCode >= 300)
        {
            Logger.LogRequest("Skipping non-success response", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }
        if (e.Session.HttpClient.Response.ContentType is null ||
            (!e.Session.HttpClient.Response.ContentType.Contains("json") &&
            !e.Session.HttpClient.Response.ContentType.Contains("application/atom+xml")) ||
            !e.Session.HttpClient.Response.HasBody)
        {
            Logger.LogRequest("Skipping response with unsupported body type", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        e.Session.HttpClient.Response.KeepBody = true;

        var nextLink = string.Empty;
        var bodyString = await e.Session.GetResponseBodyAsString();
        if (string.IsNullOrEmpty(bodyString))
        {
            Logger.LogRequest("Skipping empty response body", MessageType.Skipped, new LoggingContext(e.Session));
            return;
        }

        var contentType = e.Session.HttpClient.Response.ContentType;
        if (contentType.Contains("json"))
        {
            nextLink = GetNextLinkFromJson(bodyString);
        }
        else if (contentType.Contains("application/atom+xml"))
        {
            nextLink = GetNextLinkFromXml(bodyString);
        }

        if (!string.IsNullOrEmpty(nextLink))
        {
            pagingUrls.Add(nextLink);
        }
        else
        {
            Logger.LogRequest("No next link found in the response", MessageType.Skipped, new LoggingContext(e.Session));
        }
    }

    private string GetNextLinkFromJson(string responseBody)
    {
        var nextLink = string.Empty;

        try
        {
            var response = JsonSerializer.Deserialize<JsonElement>(responseBody, ProxyUtils.JsonSerializerOptions);
            if (response.TryGetProperty("@odata.nextLink", out var nextLinkProperty))
            {
                nextLink = nextLinkProperty.GetString() ?? string.Empty;
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "An error has occurred while parsing the response body");
        }

        return nextLink;
    }

    private string GetNextLinkFromXml(string responseBody)
    {
        var nextLink = string.Empty;

        try
        {
            var doc = XDocument.Parse(responseBody);
            nextLink = doc
              .Descendants()
              .Where(e => e.Name.LocalName == "link" && e.Attribute("rel")?.Value == "next")
              .FirstOrDefault()
              ?.Attribute("href")?.Value ?? string.Empty;
        }
        catch (Exception e)
        {
            Logger.LogError(e.Message);
        }

        return nextLink;
    }

    private static bool IsODataPagingUrl(Uri uri) =>
      uri.Query.Contains("$skip") ||
      uri.Query.Contains("%24skip") ||
      uri.Query.Contains("$skiptoken") ||
      uri.Query.Contains("%24skiptoken");

    private static string BuildIncorrectPagingUrlMessage() =>
        "This paging URL seems to be created manually and is not aligned with paging information from the API. This could lead to incorrect data in your app. For more information about paging see https://aka.ms/devproxy/guidance/paging";
}
