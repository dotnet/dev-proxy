// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy.Plugins.Guidance;

public sealed class ODSPSearchGuidancePlugin(
    ILogger<ODSPSearchGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(ODSPSearchGuidancePlugin);

    public override Task BeforeRequestAsync(ProxyRequestArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(BeforeRequestAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new(e.Session));
            return Task.CompletedTask;
        }

        if (WarnDeprecatedSearch(e.Session))
        {
            Logger.LogRequest(BuildUseGraphSearchMessage(), MessageType.Warning, new LoggingContext(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(BeforeRequestAsync));
        return Task.CompletedTask;
    }

    private bool WarnDeprecatedSearch(SessionEventArgs session)
    {
        var request = session.HttpClient.Request;
        if (!ProxyUtils.IsGraphRequest(request) ||
            request.Method != "GET")
        {
            Logger.LogRequest("Not a Microsoft Graph GET request", MessageType.Skipped, new(session));
            return false;
        }

        // graph.microsoft.com/{version}/drives/{drive-id}/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/groups/{group-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/me/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/sites/{site-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/users/{user-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/sites?search={query}
        if (request.RequestUri.AbsolutePath.Contains("/search(q=", StringComparison.OrdinalIgnoreCase) ||
            (request.RequestUri.AbsolutePath.EndsWith("/sites", StringComparison.OrdinalIgnoreCase) &&
            request.RequestUri.Query.Contains("search=", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        else
        {
            Logger.LogRequest("Not a SharePoint search request", MessageType.Skipped, new(session));
            return false;
        }
    }

    private static string BuildUseGraphSearchMessage() =>
        $"To get the best search experience, use the Microsoft Search APIs in Microsoft Graph. More info at https://aka.ms/devproxy/guidance/odspsearch";
}
