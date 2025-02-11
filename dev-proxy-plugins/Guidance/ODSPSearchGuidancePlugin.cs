// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.EventArguments;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class ODSPSearchGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(ODSPSearchGuidancePlugin);

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
        if (string.Equals(e.Session.HttpClient.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.Session));
            return Task.CompletedTask;
        }

        if (WarnDeprecatedSearch(e.Session))
        {
            Logger.LogRequest(BuildUseGraphSearchMessage(), MessageType.Warning, new LoggingContext(e.Session));
        }

        return Task.CompletedTask;
    }

    private bool WarnDeprecatedSearch(SessionEventArgs session)
    {
        Request request = session.HttpClient.Request;
        if (!ProxyUtils.IsGraphRequest(request) ||
            request.Method != "GET")
        {
            Logger.LogRequest("Not a Microsoft Graph GET request", MessageType.Skipped, new LoggingContext(session));
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
            Logger.LogRequest("Not a SharePoint search request", MessageType.Skipped, new LoggingContext(session));
            return false;
        }
    }

    private static string BuildUseGraphSearchMessage() => 
        $"To get the best search experience, use the Microsoft Search APIs in Microsoft Graph. More info at https://aka.ms/devproxy/guidance/odspsearch";
}
