// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevProxy.Plugins.Reporters;

public class PlainTextReporter : BaseReporter
{
    public PlainTextReporter(
        ILogger<PlainTextReporter> logger,
        ISet<UrlToWatch> urlsToWatch) : base(logger, urlsToWatch)
    {
    }

    [ActivatorUtilitiesConstructor]
    public PlainTextReporter(
        ILogger<PlainTextReporter> logger,
        ISet<UrlToWatch> urlsToWatch,
        IProxyConfiguration proxyConfiguration) : base(logger, urlsToWatch, proxyConfiguration)
    {
    }

    public override string Name => nameof(PlainTextReporter);
    public override string FileExtension => ".txt";

    protected override string? GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Transforming {Report}...", report.Key);

        var reportData = report.Value;
        if (reportData is IPlainTextReport markdownReport)
        {
            return markdownReport.ToPlainText();
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