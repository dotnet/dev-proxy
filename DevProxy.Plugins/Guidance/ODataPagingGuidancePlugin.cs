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

    public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestLogAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }
        if (args.Request.Method != HttpMethod.Get)
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        if (args.Request.RequestUri != null && IsODataPagingUrl(args.Request.RequestUri))
        {
            if (!pagingUrls.Contains(args.Request.RequestUri.ToString()))
            {
                Logger.LogRequest(BuildIncorrectPagingUrlMessage(), MessageType.Warning, args.Request);
            }
            else
            {
                Logger.LogRequest("Paging URL is correct", MessageType.Skipped, args.Request);
            }
        }
        else
        {
            Logger.LogRequest("Not an OData paging URL", MessageType.Skipped, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestLogAsync));
        return Task.CompletedTask;
    };

    public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => async (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnResponseLogAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return;
        }
        if (args.Request.Method != HttpMethod.Get)
        {
            Logger.LogRequest("Skipping non-GET request", MessageType.Skipped, args.Request);
            return;
        }
        if ((int)args.Response.StatusCode >= 300)
        {
            Logger.LogRequest("Skipping non-success response", MessageType.Skipped, args.Request);
            return;
        }

        var mediaType = args.Response.Content?.Headers?.ContentType?.MediaType;
        if (mediaType is null ||
            (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogRequest("Skipping response with unsupported body type", MessageType.Skipped, args.Request);
            return;
        }

        if (args.Response.Content is null)
        {
            Logger.LogRequest("Skipping response with no content", MessageType.Skipped, args.Request);
            return;
        }

        var nextLink = string.Empty;
        var bodyString = await args.Response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(bodyString))
        {
            Logger.LogRequest("Skipping empty response body", MessageType.Skipped, args.Request);
            return;
        }

        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            nextLink = GetNextLinkFromJson(bodyString);
        }
        else if (mediaType.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase))
        {
            nextLink = GetNextLinkFromXml(bodyString);
        }

        if (!string.IsNullOrEmpty(nextLink))
        {
            pagingUrls.Add(nextLink);
        }
        else
        {
            Logger.LogRequest("No next link found in the response", MessageType.Skipped, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(OnResponseLogAsync));
    };

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
