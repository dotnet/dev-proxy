// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporters;

public class MarkdownReporter(
    ILogger<MarkdownReporter> logger,
    ISet<UrlToWatch> urlsToWatch) : BaseReporter(logger, urlsToWatch)
{
    public override string Name => nameof(MarkdownReporter);
    public override string FileExtension => ".md";

    protected override string? GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Transforming {Report}...", report.Key);

        var reportData = report.Value;
        if (reportData is IMarkdownReport markdownReport)
        {
            return markdownReport.ToMarkdown();
        }
        else
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("No transformer found for {ReportType}", reportData.GetType().Name);
            }
            return null;
        }
    }
}