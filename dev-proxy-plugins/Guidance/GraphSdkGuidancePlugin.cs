// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.DevProxy.Abstractions;
using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins.Guidance;

public class GraphSdkGuidancePlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(GraphSdkGuidancePlugin);

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.AfterResponse += OnAfterResponseAsync;
    }

    private Task OnAfterResponseAsync(object? sender, ProxyResponseArgs e)
    {
        Request request = e.Session.HttpClient.Request;
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

        // only show the message if there is an error.
        if (e.Session.HttpClient.Response.StatusCode >= 400)
        {
            if (WarnNoSdk(request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkForErrorsMessage(request), MessageType.Tip, new LoggingContext(e.Session));
            }
            else
            {
                Logger.LogRequest("Request issued using SDK", MessageType.Skipped, new LoggingContext(e.Session));
            }
        }
        else
        {
            Logger.LogRequest("Skipping non-error response", MessageType.Skipped, new LoggingContext(e.Session));
        }

        return Task.CompletedTask;
    }

    private static bool WarnNoSdk(Request request) => ProxyUtils.IsGraphRequest(request) && !ProxyUtils.IsSdkRequest(request);
}
