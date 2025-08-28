// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Plugins.Models;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class MinimalPermissionsGuidancePluginReportApiResult
{
    public required string ApiName { get; init; }
    public required IEnumerable<string> ExcessivePermissions { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    public required IEnumerable<string> Requests { get; init; }
    public required IEnumerable<string> TokenPermissions { get; init; }
    public required bool UsesMinimalPermissions { get; init; }
}

public sealed class MinimalPermissionsGuidancePluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<ApiPermissionError> Errors { get; init; }
    public required IEnumerable<MinimalPermissionsGuidancePluginReportApiResult> Results { get; init; }
    public required IEnumerable<string> UnmatchedRequests { get; init; }
    public IEnumerable<string>? ExcludedPermissions { get; set; }

    public string? ToMarkdown()
    {
        if (!Results.Any() && !UnmatchedRequests.Any() && !Errors.Any() && ExcludedPermissions?.Any() != true)
        {
            return "No permissions information to report.";
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine("# Minimal Permissions Report")
            .AppendLine();

        foreach (var result in Results)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## API: {result.ApiName}")
                .AppendLine()
                .AppendLine("### Requests")
                .AppendLine()
                .AppendJoin(Environment.NewLine, result.Requests.Select(r => $"- {r}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Minimal permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, result.MinimalPermissions.Select(p => $"- `{p}`"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Permissions on the token")
                .AppendLine()
                .AppendJoin(Environment.NewLine, result.TokenPermissions.Select(p => $"- `{p}`"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Excessive permissions");

            _ = result.UsesMinimalPermissions
                ? sb.AppendLine()
                    .AppendLine("The token has the minimal permissions required.")
                : sb.AppendLine()
                    .AppendLine("The following permissions included in the token are unnecessary:")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, result.ExcessivePermissions.Select(p => $"- `{p}`"))
                    .AppendLine();

            _ = sb.AppendLine();
        }

        if (UnmatchedRequests.Any())
        {
            _ = sb.AppendLine("## Unmatched Requests")
                .AppendLine()
                .AppendLine("The following requests could not be matched:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, UnmatchedRequests.Select(r => $"- {r}"))
                .AppendLine()
                .AppendLine();
        }

        if (Errors.Any())
        {
            _ = sb.AppendLine("## Errors")
                .AppendLine()
                .AppendLine("| Request | Error |")
                .AppendLine("| --------| ----- |")
                .AppendJoin(Environment.NewLine, Errors.Select(error => $"| {error.Request} | {error.Error} |"))
                .AppendLine()
                .AppendLine();
        }

        if (ExcludedPermissions?.Any() == true)
        {
            _ = sb.AppendLine("## Excluded Permissions")
                .AppendLine()
                .AppendLine("The following permissions were excluded from the analysis:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, ExcludedPermissions.Select(p => $"- `{p}`"))
                .AppendLine();
        }

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        if (!Results.Any() && !UnmatchedRequests.Any() && !Errors.Any() && ExcludedPermissions?.Any() != true)
        {
            return "No permissions information to report.";
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine("Minimal Permissions Report")
              .AppendLine("==========================")
              .AppendLine();

        foreach (var result in Results)
        {
            var apiTitle = $"API: {result.ApiName}";
            _ = sb.AppendLine()
                .AppendLine(apiTitle)
                .AppendLine(new string('-', apiTitle.Length))
                .AppendLine();

            _ = sb.AppendLine("Requests:")
                .AppendJoin(Environment.NewLine, result.Requests.Select(r => $"- {r}")).AppendLine()
                .AppendLine();

            _ = sb.AppendLine("Minimal permissions:")
                .AppendJoin(", ", result.MinimalPermissions).AppendLine()
                .AppendLine();

            _ = sb.AppendLine("Permissions on the token:")
                .AppendJoin(", ", result.TokenPermissions).AppendLine()
                .AppendLine();

            _ = sb.AppendLine("Excessive permissions")
                .AppendLine("---------------------")
                .AppendLine();

            _ = result.UsesMinimalPermissions
                ? sb.AppendLine("The token has the minimal permissions required.")
                : sb.AppendLine("The following permissions included in the token are unnecessary:")
                    .AppendJoin(", ", result.ExcessivePermissions).AppendLine();
            _ = sb.AppendLine();
        }

        if (UnmatchedRequests.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("Unmatched Requests")
                .AppendLine("------------------")
                .AppendLine()
                .AppendLine("The following requests could not be matched:")
                .AppendJoin(Environment.NewLine, UnmatchedRequests.Select(r => $"- {r}")).AppendLine()
                .AppendLine();
        }

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("Errors")
                .AppendLine("------")
                .AppendLine()
                .AppendLine("The following errors occurred while finding permissions for requests:")
                .AppendJoin(Environment.NewLine, Errors.Select(error => $"- For request '{error.Request}': {error.Error}"))
                .AppendLine()
                .AppendLine();
        }

        if (ExcludedPermissions?.Any() == true)
        {
            _ = sb.AppendLine()
                .AppendLine("Excluded Permissions")
                .AppendLine("--------------------")
                .AppendLine()
                .AppendLine("The following permissions were excluded from the analysis:")
                .AppendJoin(", ", ExcludedPermissions).AppendLine();
        }

        return sb.ToString();
    }
}