// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporting;

public sealed class UrlDiscoveryPlugin(
    ILogger<UrlDiscoveryPlugin> logger,
    ISet<UrlToWatch> urlsToWatch) : BaseReportingPlugin(logger, urlsToWatch)
{
    public override string Name => nameof(UrlDiscoveryPlugin);

    public override Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogRequest("No messages recorded", MessageType.Skipped);
            return Task.CompletedTask;
        }

        var requestLogs = e.RequestLogs
            .Where(l => ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context?.Session.HttpClient.Request.RequestUri.AbsoluteUri ?? ""));

        UrlDiscoveryPluginReport report = new()
        {
            Data =
            [
                .. requestLogs
                    .Where(log => log.Context is not null)
                    .Select(log => log.Context!.Session.HttpClient.Request.RequestUri.ToString()).Distinct().Order()
            ]
        };

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
        return Task.CompletedTask;
    }
}
