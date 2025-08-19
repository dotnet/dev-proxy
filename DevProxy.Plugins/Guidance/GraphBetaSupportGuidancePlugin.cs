// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Guidance;

public sealed class GraphBetaSupportGuidancePlugin(
    ILogger<GraphBetaSupportGuidancePlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BasePlugin(logger, urlsToWatch)
{
    public override string Name => nameof(GraphBetaSupportGuidancePlugin);

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
        if (!ProxyUtils.IsGraphBetaRequest(request))
        {
            Logger.LogRequest("Not a Microsoft Graph beta request", MessageType.Skipped, args.Request);
            return Task.CompletedTask;
        }

        Logger.LogRequest(BuildBetaSupportMessage(), MessageType.Warning, args.Request);
        Logger.LogTrace("Left {Name}", nameof(OnRequestLogAsync));
        return Task.CompletedTask;
    };

    private static string GetBetaSupportGuidanceUrl() => "https://aka.ms/devproxy/guidance/beta-support";
    private static string BuildBetaSupportMessage() =>
        $"Don't use beta APIs in production because they can change or be deprecated. More info at {GetBetaSupportGuidanceUrl()}";
}
