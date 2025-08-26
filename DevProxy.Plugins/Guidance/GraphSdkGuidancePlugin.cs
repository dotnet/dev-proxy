// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphSdkGuidancePlugin(
    ILogger<GraphSdkGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphSdkGuidancePlugin);

    public override Func<ResponseArguments, CancellationToken, Task>? ProvideResponseGuidanceAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(ProvideResponseGuidanceAsync));

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

        // only show the message if there is an error.
        if ((int)args.Response.StatusCode >= 400)
        {
            if (WarnNoSdk(args.Request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkForErrorsMessage(), MessageType.Tip, args.Request);
            }
            else
            {
                Logger.LogRequest("Request issued using SDK", MessageType.Skipped, args.Request);
            }
        }
        else
        {
            Logger.LogRequest("Skipping non-error response", MessageType.Skipped, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(ProvideResponseGuidanceAsync));
        return Task.CompletedTask;
    };

    private static bool WarnNoSdk(HttpRequestMessage request) =>
        ProxyUtils.IsGraphRequest(request) && !ProxyUtils.IsSdkRequest(request);
}
