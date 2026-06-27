// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphSdkGuidancePlugin(
    ILogger<GraphSdkGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphSdkGuidancePlugin);

    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        var request = e.ProxySession.Request;
        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }
        if (string.Equals(e.ProxySession.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, new LoggingContext(e.ProxySession));
            return Task.CompletedTask;
        }

        // only show the message if there is an error.
        if ((int)e.ProxySession.Response!.StatusCode >= 400)
        {
            if (WarnNoSdk(request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkForErrorsMessage(), MessageType.Tip, new LoggingContext(e.ProxySession));
            }
            else
            {
                Logger.LogRequest("Request issued using SDK", MessageType.Skipped, new LoggingContext(e.ProxySession));
            }
        }
        else
        {
            Logger.LogRequest("Skipping non-error response", MessageType.Skipped, new LoggingContext(e.ProxySession));
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    private static bool WarnNoSdk(IHttpRequest request) =>
        ProxyUtils.IsGraphRequest(request) && !ProxyUtils.IsSdkRequest(request);
}
