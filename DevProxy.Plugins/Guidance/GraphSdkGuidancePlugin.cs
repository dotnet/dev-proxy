// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Models;
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

    public override Func<ResponseArguments, CancellationToken, Task>? OnResponseLogAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnResponseLogAsync));

        if (!ProxyUtils.MatchesUrlToWatch(UrlsToWatch, args.HttpRequestMessage.RequestUri))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, args.HttpRequestMessage);
            return Task.CompletedTask;
        }
        if (args.HttpRequestMessage.Method == HttpMethod.Options)
        {
            Logger.LogRequest("Skipping OPTIONS request", MessageType.Skipped, args.HttpRequestMessage);
            return Task.CompletedTask;
        }

        // only show the message if there is an error.
        if ((int)args.HttpResponseMessage.StatusCode >= 400)
        {
            if (WarnNoSdk(args.HttpRequestMessage))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkForErrorsMessage(), MessageType.Tip, args.HttpRequestMessage);
            }
            else
            {
                Logger.LogRequest("Request issued using SDK", MessageType.Skipped, args.HttpRequestMessage);
            }
        }
        else
        {
            Logger.LogRequest("Skipping non-error response", MessageType.Skipped, args.HttpRequestMessage);
        }

        Logger.LogTrace("Left {Name}", nameof(OnResponseLogAsync));
        return Task.CompletedTask;
    };

    private static bool WarnNoSdk(HttpRequestMessage request) =>
        ProxyUtils.IsGraphRequest(request) && !ProxyUtils.IsSdkRequest(request);
}
