// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevProxy.Plugins.Reporters;

public class JsonReporter(
    ILogger<JsonReporter> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyStorage proxyStorage) : BaseReporter(logger, urlsToWatch, proxyStorage)
{
    private string _fileExtension = ".json";

    public override string Name => nameof(JsonReporter);
    public override string FileExtension => _fileExtension;

    protected override string GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Serializing report {ReportKey}...", report.Key);

        var reportData = report.Value;
        if (reportData is IJsonReport jsonReport)
        {
            Logger.LogDebug("{ReportKey} implements IJsonReport, converting to JSON...", report.Key);
            reportData = jsonReport.ToJson();
            _fileExtension = jsonReport.FileExtension;
        }
        else
        {
            Logger.LogDebug("No transformer found for {ReportType}", reportData.GetType().Name);
        }

        if (reportData is string strVal)
        {
            Logger.LogDebug("{ReportKey} is a string. Checking if it's JSON...", report.Key);

            try
            {
                _ = JsonSerializer.Deserialize<object>(strVal, ProxyUtils.JsonSerializerOptions);
                Logger.LogDebug("{ReportKey} is already JSON, ignore", report.Key);
                // already JSON, ignore
                return strVal;
            }
            catch
            {
                Logger.LogDebug("{ReportKey} is not JSON, serializing...", report.Key);
            }
        }

        return JsonSerializer.Serialize(reportData, ProxyUtils.JsonSerializerOptions);
    }
}