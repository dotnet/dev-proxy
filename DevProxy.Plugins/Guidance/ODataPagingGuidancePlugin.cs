// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Xml.Linq;

namespace DevProxy.Plugins.Guidance;

public sealed class ODataPagingGuidancePlugin(
    ILogger<ODataPagingGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    private readonly IList<string> pagingUrls = [];

    public override string Name => nameof(ODataPagingGuidancePlugin);

    public override Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (!string.Equals(e.Session.HttpClient.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        if (IsODataPagingUrl(e.Session.HttpClient.Request.RequestUri))
        {
            if (!pagingUrls.Contains(e.Session.HttpClient.Request.Url))
            {
                Logger.LogRequest(BuildIncorrectPagingUrlMessage(), MessageType.Warning, new(e.Session));
            }
            else
            {
                Logger.LogRequest("Paging URL is correct", MessageType.Skipped, new(e.Session));
            }
        }
        else
        {
            Logger.LogRequest("Not an OData paging URL", MessageType.Skipped, new(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    public override async Task BeforeResponseAsync(ProxyResponseArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return;
        }
        if (!string.Equals(e.Session.HttpClient.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new(e.Session));
            return;
        }
        if (e.Session.HttpClient.Response.StatusCode >= 300)
        {
            Logger.LogRequest("Skipping non-success response", MessageType.Skipped, new(e.Session));
            return;
        }
        if (e.Session.HttpClient.Response.ContentType is null ||
            (!e.Session.HttpClient.Response.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !e.Session.HttpClient.Response.ContentType.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase)) ||
            !e.Session.HttpClient.Response.HasBody)
        {
            Logger.LogRequest("Skipping response with unsupported body type", MessageType.Skipped, new(e.Session));
            return;
        }

        e.Session.HttpClient.Response.KeepBody = true;

        var nextLink = string.Empty;
        var bodyString = await e.Session.GetResponseBodyAsString();
        if (string.IsNullOrEmpty(bodyString))
        {
            Logger.LogRequest("Skipping empty response body", MessageType.Skipped, new(e.Session));
            return;
        }

        var contentType = e.Session.HttpClient.Response.ContentType;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            nextLink = GetNextLinkFromJson(bodyString);
        }
        else if (contentType.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase))
        {
            nextLink = GetNextLinkFromXml(bodyString);
        }

        if (!string.IsNullOrEmpty(nextLink))
        {
            pagingUrls.Add(nextLink);
        }
        else
        {
            Logger.LogRequest("No next link found in the response", MessageType.Skipped, new(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeResponseAsync));
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
              .FirstOrDefault(e => e.Name.LocalName == "link" && e.Attribute("rel")?.Value == "next")
              ?.Attribute("href")?.Value ?? string.Empty;
        }
        catch (Exception e)
        {
            Logger.LogError("{Error}", e.Message);
        }

        return nextLink;
    }

    private static bool IsODataPagingUrl(Uri uri) =>
      uri.Query.Contains("$skip", StringComparison.OrdinalIgnoreCase) ||
      uri.Query.Contains("%24skip", StringComparison.OrdinalIgnoreCase) ||
      uri.Query.Contains("$skiptoken", StringComparison.OrdinalIgnoreCase) ||
      uri.Query.Contains("%24skiptoken", StringComparison.OrdinalIgnoreCase);

    private static string BuildIncorrectPagingUrlMessage() =>
        "This paging URL seems to be created manually and is not aligned with paging information from the API. This could lead to incorrect data in your app. For more information about paging see https://aka.ms/devproxy/guidance/paging";
}
