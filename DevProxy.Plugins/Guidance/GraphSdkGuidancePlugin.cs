// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Http;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphSdkGuidancePlugin(
    ILogger<GraphSdkGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphSdkGuidancePlugin);

    public override Task AfterResponseAsync(ProxyResponseArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.Session.HttpClient.Request;
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

        // only show the message if there is an error.
        if (e.Session.HttpClient.Response.StatusCode >= 400)
        {
            if (WarnNoSdk(request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkForErrorsMessage(), MessageType.Tip, new(e.Session));
            }
            else
            {
                Logger.LogRequest("Request issued using SDK", MessageType.Skipped, new(e.Session));
            }
        }
        else
        {
            Logger.LogRequest("Skipping non-error response", MessageType.Skipped, new(e.Session));
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    private static bool WarnNoSdk(Request request) =>
        ProxyUtils.IsGraphRequest(request) && !ProxyUtils.IsSdkRequest(request);
}
