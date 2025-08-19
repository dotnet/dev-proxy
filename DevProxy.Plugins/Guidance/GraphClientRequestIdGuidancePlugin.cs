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

public sealed class GraphClientRequestIdGuidancePlugin(
    ILogger<GraphClientRequestIdGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphClientRequestIdGuidancePlugin);

    public override Func<RequestArguments, CancellationToken, Task>? OnRequestLogAsync => (args, cancellationToken) =>
    {
        Logger.LogTrace("{Method} called", nameof(OnRequestLogAsync));

        ArgumentNullException.ThrowIfNull(args);

        var request = args.Request;
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

        if (WarnNoClientRequestId(request))
        {
            Logger.LogRequest(BuildAddClientRequestIdMessage(), MessageType.Warning, args.Request);

            if (!ProxyUtils.IsSdkRequest(request))
            {
                Logger.LogRequest(MessageUtils.BuildUseSdkMessage(), MessageType.Tip, args.Request);
            }
        }
        else
        {
            Logger.LogRequest("client-request-id header present", MessageType.Skipped, args.Request);
        }

        Logger.LogTrace("Left {Name}", nameof(OnRequestLogAsync));
        return Task.CompletedTask;
    };

    private static bool WarnNoClientRequestId(HttpRequestMessage request) =>
        ProxyUtils.IsGraphRequest(request) &&
        !request.Headers.Contains("client-request-id");

    private static string GetClientRequestIdGuidanceUrl() => "https://aka.ms/devproxy/guidance/client-request-id";
    private static string BuildAddClientRequestIdMessage() =>
        $"To help Microsoft investigate errors, to each request to Microsoft Graph add the client-request-id header with a unique GUID. More info at {GetClientRequestIdGuidanceUrl()}";
}
