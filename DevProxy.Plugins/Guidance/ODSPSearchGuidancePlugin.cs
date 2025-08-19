// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Guidance;

public sealed class ODSPSearchGuidancePlugin(
    ILogger<ODSPSearchGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(ODSPSearchGuidancePlugin);

    public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestLogAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.Request.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }
        if (args.Request.Method == HttpMethod.Options)
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        if (WarnDeprecatedSearch(args.Request))
        {
            Logger.LogRequest(BuildUseGraphSearchMessage(), MessageType.Warning, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestLogAsync));
        return Task.CompletedTask;
    };

    private bool WarnDeprecatedSearch(HttpRequestMessage request)
    {
        if (!ProxyUtils.IsGraphRequest(request) ||
            request.Method != HttpMethod.Get)
        {
            Logger.LogRequest("Not a Microsoft Graph GET request", MessageType.Skipped, request);
            return false;
        }

        // graph.microsoft.com/{version}/drives/{drive-id}/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/groups/{group-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/me/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/sites/{site-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/users/{user-id}/drive/root/search(q='{search-text}')
        // graph.microsoft.com/{version}/sites?search={query}
        if (request.RequestUri != null &&
            (request.RequestUri.AbsolutePath.Contains("/search(q=", StringComparison.OrdinalIgnoreCase) ||
            (request.RequestUri.AbsolutePath.EndsWith("/sites", StringComparison.OrdinalIgnoreCase) &&
            request.RequestUri.Query.Contains("search=", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }
        else
        {
            Logger.LogRequest("Not a SharePoint search request", MessageType.Skipped, request);
            return false;
        }
    }

    private static string BuildUseGraphSearchMessage() =>
        $"To get the best search experience, use the Microsoft Search APIs in Microsoft Graph. More info at https://aka.ms/devproxy/guidance/odspsearch";
}
