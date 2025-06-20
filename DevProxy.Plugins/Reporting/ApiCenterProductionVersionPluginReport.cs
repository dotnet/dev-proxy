// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace DevProxy.Plugins.Reporting;

public enum ApiCenterProductionVersionPluginReportItemStatus
{
    NotRegistered,
    NonProduction,
    Production
}

public sealed class ApiCenterProductionVersionPluginReportItem
{
    public required string Method { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ApiCenterProductionVersionPluginReportItemStatus Status { get; init; }
    public required string Url { get; init; }
    public string? Recommendation { get; init; }
}

public sealed class ApiCenterProductionVersionPluginReport :
    List<ApiCenterProductionVersionPluginReportItem>, IMarkdownReport, IPlainTextReport
{
    public string? ToMarkdown()
    {
        static string getReadableApiStatus(ApiCenterProductionVersionPluginReportItemStatus status)
        {
            return status switch
            {
                ApiCenterProductionVersionPluginReportItemStatus.NotRegistered => "🛑 Not registered",
                ApiCenterProductionVersionPluginReportItemStatus.NonProduction => "⚠️ Non-production",
                ApiCenterProductionVersionPluginReportItemStatus.Production => "✅ Production",
                _ => "Unknown"
            };
        }

        var groupedPerStatus = this
            .GroupBy(a => a.Status)
            .OrderBy(g => (int)g.Key);

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Azure API Center lifecycle report")
            .AppendLine();

        foreach (var group in groupedPerStatus)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## {getReadableApiStatus(group.Key)} APIs")
                .AppendLine();

            _ = group.Key == ApiCenterProductionVersionPluginReportItemStatus.NonProduction
                ? sb.AppendLine("API|Recommendation")
                    .AppendLine("---|------------")
                    .AppendJoin(Environment.NewLine, group
                        .OrderBy(a => a.Url)
                        .Select(a => $"{a.Method} {a.Url}|{a.Recommendation ?? ""}"))
                    .AppendLine()
                : sb.AppendJoin(Environment.NewLine, group
                        .OrderBy(a => a.Url)
                        .Select(a => $"- {a.Method} {a.Url}"))
                    .AppendLine();

            _ = sb.AppendLine();
        }

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        static string getReadableApiStatus(ApiCenterProductionVersionPluginReportItemStatus status)
        {
            return status switch
            {
                ApiCenterProductionVersionPluginReportItemStatus.NotRegistered => "Not registered",
                ApiCenterProductionVersionPluginReportItemStatus.NonProduction => "Non-production",
                ApiCenterProductionVersionPluginReportItemStatus.Production => "Production",
                _ => "Unknown"
            };
        }

        var groupedPerStatus = this
            .GroupBy(a => a.Status)
            .OrderBy(g => (int)g.Key);

        var sb = new StringBuilder();

        foreach (var group in groupedPerStatus)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{getReadableApiStatus(group.Key)} APIs:")
                .AppendLine()

                .AppendJoin(Environment.NewLine, group.Select(a => $"  {a.Method} {a.Url}"))
                .AppendLine()
                .AppendLine();
        }

        return sb.ToString();
    }
}