// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Plugins.Models;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class ApiCenterMinimalPermissionsPluginReportApiResult
{
    public required string ApiDefinitionId { get; init; }
    public required string ApiId { get; init; }
    public required string ApiName { get; init; }
    public required IEnumerable<string> ExcessivePermissions { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    public required IEnumerable<string> Requests { get; init; }
    public required IEnumerable<string> TokenPermissions { get; init; }
    public required bool UsesMinimalPermissions { get; init; }
}

public sealed class ApiCenterMinimalPermissionsPluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<ApiPermissionError> Errors { get; init; }
    public required IEnumerable<ApiCenterMinimalPermissionsPluginReportApiResult> Results { get; init; }
    public required IEnumerable<string> UnmatchedRequests { get; init; }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("# Azure API Center minimal permissions report")
            .AppendLine();

        _ = sb.AppendLine("## ℹ️ Summary")
            .AppendLine()
            .AppendLine("<table>")
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🔎 APIs inspected</td><td align=\"right\">{0}</td></tr>{1}", Results.Count(), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🔎 Requests inspected</td><td align=\"right\">{0}</td></tr>{1}", Results.Sum(r => r.Requests.Count()), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>✅ APIs called using minimal permissions</td><td align=\"right\">{0}</td></tr>{1}", Results.Count(r => r.UsesMinimalPermissions), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🛑 APIs called using excessive permissions</td><td align=\"right\">{0}</td></tr>{1}", Results.Count(r => !r.UsesMinimalPermissions), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>⚠️ Unmatched requests</td><td align=\"right\">{0}</td></tr>{1}", UnmatchedRequests.Count(), Environment.NewLine)
            .AppendFormat(CultureInfo.InvariantCulture, "<tr><td>🛑 Errors</td><td align=\"right\">{0}</td></tr>{1}", Errors.Count(), Environment.NewLine)
            .AppendLine("</table>")
            .AppendLine();

        _ = sb.AppendLine("## 🔌 APIs")
            .AppendLine();

        if (Results.Any())
        {
            foreach (var apiResult in Results)
            {
                _ = sb.AppendFormat(CultureInfo.InvariantCulture, "### {0}{1}", apiResult.ApiName, Environment.NewLine)
                    .AppendLine()
                    .AppendFormat(CultureInfo.InvariantCulture, apiResult.UsesMinimalPermissions ? "✅ Called using minimal permissions{0}" : "🛑 Called using excessive permissions{0}", Environment.NewLine)
                    .AppendLine()
                    .AppendLine("#### Permissions")
                    .AppendLine()
                    .AppendFormat(CultureInfo.InvariantCulture, "- Minimal permissions: {0}{1}", string.Join(", ", apiResult.MinimalPermissions.Order().Select(p => $"`{p}`")), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Permissions on the token: {0}{1}", string.Join(", ", apiResult.TokenPermissions.Order().Select(p => $"`{p}`")), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Excessive permissions: {0}{1}", apiResult.ExcessivePermissions.Any() ? string.Join(", ", apiResult.ExcessivePermissions.Order().Select(p => $"`{p}`")) : "none", Environment.NewLine)
                    .AppendLine()
                    .AppendLine("#### Requests")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}")).AppendLine()
                    .AppendLine();
            }
        }
        else
        {
            _ = sb.AppendLine("No APIs found.")
                .AppendLine();
        }

        _ = sb.AppendLine("## ⚠️ Unmatched requests")
            .AppendLine();

        _ = UnmatchedRequests.Any()
            ? sb.AppendLine("The following requests were not matched to any API in API Center:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, UnmatchedRequests
                    .Select(r => $"- {r}").Order()).AppendLine()
                .AppendLine()
            : sb.AppendLine("No unmatched requests found.")
                .AppendLine();

        _ = sb.AppendLine("## 🛑 Errors")
            .AppendLine();

        _ = Errors.Any()
            ? sb.AppendLine("The following errors occurred while determining minimal permissions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors
                    .OrderBy(o => o.Request)
                    .Select(e => $"- `{e.Request}`: {e.Error}")).AppendLine()
                .AppendLine()
            : sb.AppendLine("No errors occurred.");

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("Azure API Center minimal permissions report")
            .AppendLine()

            .AppendLine("APIS")
            .AppendLine();

        if (Results.Any())
        {
            foreach (var apiResult in Results)
            {
                _ = sb.AppendFormat(CultureInfo.InvariantCulture, "{0}{1}", apiResult.ApiName, Environment.NewLine)
                    .AppendLine()
                    .AppendLine(apiResult.UsesMinimalPermissions ? "v Called using minimal permissions" : "x Called using excessive permissions")
                    .AppendLine()
                    .AppendLine("Permissions")
                    .AppendLine()
                    .AppendFormat(CultureInfo.InvariantCulture, "- Minimal permissions: {0}{1}", string.Join(", ", apiResult.MinimalPermissions.Order()), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Permissions on the token: {0}{1}", string.Join(", ", apiResult.TokenPermissions.Order()), Environment.NewLine)
                    .AppendFormat(CultureInfo.InvariantCulture, "- Excessive permissions: {0}{1}", apiResult.ExcessivePermissions.Any() ? string.Join(", ", apiResult.ExcessivePermissions.Order()) : "none", Environment.NewLine)
                    .AppendLine()
                    .AppendLine("Requests")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, apiResult.Requests.Select(r => $"- {r}")).AppendLine()
                    .AppendLine();
            }
        }
        else
        {
            _ = sb.AppendLine("No APIs found.")
                .AppendLine();
        }

        _ = sb.AppendLine("UNMATCHED REQUESTS")
            .AppendLine();

        _ = UnmatchedRequests.Any()
            ? sb.AppendLine("The following requests were not matched to any API in API Center:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, UnmatchedRequests
                    .Select(r => $"- {r}").Order()).AppendLine()
                .AppendLine()
            : sb.AppendLine("No unmatched requests found.")
                .AppendLine();

        _ = sb.AppendLine("ERRORS")
            .AppendLine();

        _ = Errors.Any()
            ? sb.AppendLine("The following errors occurred while determining minimal permissions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors
                    .OrderBy(o => o.Request)
                    .Select(e => $"- `{e.Request}`: {e.Error}")).AppendLine()
                .AppendLine()
            : sb.AppendLine("No errors occurred.");

        return sb.ToString();
    }
}

