// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Reporters;

public abstract class BaseReporter(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseProxyPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public virtual string FileExtension => throw new NotImplementedException();

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    protected abstract string? GetReport(KeyValuePair<string, object> report);

    protected virtual Task AfterRecordingStopAsync(object sender, RecordingArgs e)
    {
        if (!e.GlobalData.TryGetValue(ProxyUtils.ReportsKey, out object? value) ||
            value is not Dictionary<string, object> reports ||
            reports.Count == 0)
        {
            Logger.LogDebug("No reports found");
            return Task.CompletedTask;
        }

        foreach (var report in reports)
        {
            Logger.LogDebug("Transforming report {reportKey}...", report.Key);

            var reportContents = GetReport(report);

            if (string.IsNullOrEmpty(reportContents))
            {
                Logger.LogDebug("Report {reportKey} is empty, ignore", report.Key);
                continue;
            }

            var fileName = $"{report.Key}_{Name}{FileExtension}";
            Logger.LogDebug("File name for report {report}: {fileName}", report.Key, fileName);

            if (File.Exists(fileName))
            {
                Logger.LogDebug("File {fileName} already exists, appending timestamp", fileName);
                fileName = $"{report.Key}_{Name}_{DateTime.Now:yyyyMMddHHmmss}{FileExtension}";
            }

            Logger.LogInformation("Writing report {reportKey} to {fileName}...", report.Key, fileName);
            File.WriteAllText(fileName, reportContents);
        }

        return Task.CompletedTask;
    }
}