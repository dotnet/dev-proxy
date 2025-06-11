// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Plugins.Models;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class MinimalPermissionsPluginReportApiResult
{
    public required string ApiName { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    public required IEnumerable<string> Requests { get; init; }
    public required IEnumerable<string> TokenPermissions { get; init; }
}

public sealed class MinimalPermissionsPluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<ApiPermissionError> Errors { get; init; }
    public required IEnumerable<MinimalPermissionsPluginReportApiResult> Results { get; init; }
    public required IEnumerable<string> UnmatchedRequests { get; init; }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine($"# Minimal permissions report");

        foreach (var apiResult in Results)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"## API {apiResult.ApiName}:")

                .AppendLine("### Requests")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}"))
                .AppendLine()

                .AppendLine()
                .AppendLine("### Minimal permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.MinimalPermissions.Select(p => $"- {p}"))
                .AppendLine();
        }

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("## 🛑 Errors")
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following URLs:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors.Select(e => $"- {e}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine($"Minimal permissions report");

        foreach (var apiResult in Results)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"API {apiResult.ApiName}:")
                .AppendLine()
                .AppendLine("Requests:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("Minimal permissions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, apiResult.MinimalPermissions.Select(p => $"- {p}"));
        }

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following requests:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors.Select(e => $"- {e.Request}: {e.Error}"));
        }

        return sb.ToString();
    }
}