// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporters;

public abstract class BaseReporter(
    ILogger logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BasePlugin(logger, urlsToWatch)
{
    public abstract string FileExtension { get; }

    public override Func<RecordingArgs, CancellationToken, Task>? HandleRecordingStopAsync => AfterRecordingStopAsync;
    public async Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        //await base.AfterRecordingStopAsync(e, cancellationToken);

        if (!proxyStorage.GlobalData.TryGetValue(ProxyUtils.ReportsKey, out var value) ||
            value is not Dictionary<string, object> reports ||
            reports.Count == 0)
        {
            Logger.LogDebug("No reports found");
            return;
        }

        foreach (var report in reports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Logger.LogDebug("Transforming report {ReportKey}...", report.Key);

            var reportContents = GetReport(report);

            if (string.IsNullOrEmpty(reportContents))
            {
                Logger.LogDebug("Report {ReportKey} is empty, ignore", report.Key);
                continue;
            }

            var fileName = $"{report.Key}_{Name}{FileExtension}";
            Logger.LogDebug("File name for report {Report}: {FileName}", report.Key, fileName);

            if (File.Exists(fileName))
            {
                Logger.LogDebug("File {FileName} already exists, appending timestamp", fileName);
                fileName = $"{report.Key}_{Name}_{DateTime.Now:yyyyMMddHHmmss}{FileExtension}";
            }

            Logger.LogInformation("Writing report {ReportKey} to {FileName}...", report.Key, fileName);
            await File.WriteAllTextAsync(fileName, reportContents, cancellationToken);
        }

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
    }

    protected abstract string? GetReport(KeyValuePair<string, object> report);
}