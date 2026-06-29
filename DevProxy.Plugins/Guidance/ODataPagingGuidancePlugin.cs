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

    public override Task BeforeRequestAsync(ProxyRequestArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }
        if (!string.Equals(e.ProxySession.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        if (IsODataPagingUrl(e.ProxySession.Request.RequestUri))
        {
            if (!pagingUrls.Contains(e.ProxySession.Request.Url))
            {
                Logger.LogRequest(BuildIncorrectPagingUrlMessage(), MessageType.Warning, new LoggingContext(e.ProxySession));
            }
            else
            {
                Logger.LogRequest("Paging URL is correct", MessageType.Skipped, new LoggingContext(e.ProxySession));
            }
        }
        else
        {
            Logger.LogRequest("Not an OData paging URL", MessageType.Skipped, new LoggingContext(e.ProxySession));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    public override async Task BeforeResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }
        if (!string.Equals(e.ProxySession.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }
        if ((int)e.ProxySession.Response!.StatusCode >= 300)
        {
            Logger.LogRequest("Skipping non-success response", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }
        if (e.ProxySession.Response!.ContentType is null ||
            (!e.ProxySession.Response!.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !e.ProxySession.Response!.ContentType.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase)) ||
            !e.ProxySession.Response!.HasBody)
        {
            Logger.LogRequest("Skipping response with unsupported body type", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var nextLink = string.Empty;
        var bodyString = e.ProxySession.Response!.BodyString;
        if (string.IsNullOrEmpty(bodyString))
        {
            Logger.LogRequest("Skipping empty response body", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return;
        }

        var contentType = e.ProxySession.Response!.ContentType;
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
            Logger.LogRequest("No next link found in the response", MessageType.Skipped, new LoggingContext(e.ProxySession));
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
