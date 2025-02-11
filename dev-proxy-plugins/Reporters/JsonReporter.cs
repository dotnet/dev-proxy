// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.RequestLogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.Reporters;

public class JsonReporter(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReporter(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(JsonReporter);
    private string _fileExtension = ".json";
    public override string FileExtension => _fileExtension;

    private readonly Dictionary<Type, Func<object, object>> _transformers = new()
    {
        { typeof(ExecutionSummaryPluginReportByUrl), TransformExecutionSummary },
        { typeof(ExecutionSummaryPluginReportByMessageType), TransformExecutionSummary },
        { typeof(UrlDiscoveryPluginReport), TransformUrlDiscoveryReport }
    };

    protected override string GetReport(KeyValuePair<string, object> report)
    {
        Logger.LogDebug("Serializing report {reportKey}...", report.Key);

        var reportData = report.Value;
        var reportType = reportData.GetType();
        _fileExtension = reportType.Name == nameof(UrlDiscoveryPluginReport) ? ".jsonc" : ".json";

        if (_transformers.TryGetValue(reportType, out var transform))
        {
            Logger.LogDebug("Transforming {reportType} using {transform}...", reportType.Name, transform.Method.Name);
            reportData = transform(reportData);
        }
        else
        {
            Logger.LogDebug("No transformer found for {reportType}", reportType.Name);
        }

        if (reportData is string strVal)
        {
            Logger.LogDebug("{reportKey} is a string. Checking if it's JSON...", report.Key);

            try
            {
                JsonSerializer.Deserialize<object>(strVal, ProxyUtils.JsonSerializerOptions);
                Logger.LogDebug("{reportKey} is already JSON, ignore", report.Key);
                // already JSON, ignore
                return strVal;
            }
            catch
            {
                Logger.LogDebug("{reportKey} is not JSON, serializing...", report.Key);
            }
        }

        return JsonSerializer.Serialize(reportData, ProxyUtils.JsonSerializerOptions);
    }

    private static object TransformExecutionSummary(object report)
    {
        var executionSummaryReport = (ExecutionSummaryPluginReportBase)report;
        return executionSummaryReport.Data;
    }

    private static object TransformUrlDiscoveryReport(object report)
    {
        var urlDiscoveryPluginReport = (UrlDiscoveryPluginReport)report;

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Wildcards");
        sb.AppendLine("  // ");
        sb.AppendLine("  // You can use wildcards to catch multiple URLs with the same pattern.");
        sb.AppendLine("  // For example, you can use the following URL pattern to catch all API requests to");
        sb.AppendLine("  // JSON Placeholder API:");
        sb.AppendLine("  // ");
        sb.AppendLine("  // https://jsonplaceholder.typicode.com/*");
        sb.AppendLine("  // ");
        sb.AppendLine("  // Excluding URLs");
        sb.AppendLine("  // ");
        sb.AppendLine("  // You can exclude URLs with ! to prevent them from being intercepted.");
        sb.AppendLine("  // For example, you can exclude the URL https://jsonplaceholder.typicode.com/authors");
        sb.AppendLine("  // by using the following URL pattern:");
        sb.AppendLine("  // ");
        sb.AppendLine("  // !https://jsonplaceholder.typicode.com/authors");
        sb.AppendLine("  // https://jsonplaceholder.typicode.com/*");
        sb.AppendLine("  \"urlsToWatch\": [");
        sb.AppendJoin($",{Environment.NewLine}", urlDiscoveryPluginReport.Data.Select(u => $"    \"{u}\""));
        sb.AppendLine("");
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }
}