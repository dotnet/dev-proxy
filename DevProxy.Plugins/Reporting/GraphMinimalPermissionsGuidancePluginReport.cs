// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class GraphMinimalPermissionsGuidancePluginReport : IMarkdownReport, IPlainTextReport
{
    public GraphMinimalPermissionsInfo? ApplicationPermissions { get; set; }
    public GraphMinimalPermissionsInfo? DelegatedPermissions { get; set; }
    public IEnumerable<string>? ExcludedPermissions { get; set; }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("# Minimal permissions report")
            .AppendLine();

        void transformPermissionsInfo(GraphMinimalPermissionsInfo permissionsInfo, string type)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"## Minimal {type} permissions")
                .AppendLine()
                .AppendLine("### Operations")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.Operations.Select(o => $"- {o.Method} {o.Endpoint}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Minimal permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.MinimalPermissions.Select(p => $"- {p}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Permissions on the token")
                .AppendLine()
                .AppendJoin(Environment.NewLine, permissionsInfo.PermissionsFromTheToken.Select(p => $"- {p}"))
                .AppendLine()
                .AppendLine()
                .AppendLine("### Excessive permissions");

            _ = permissionsInfo.ExcessPermissions.Any()
                ? sb.AppendLine()
                    .AppendLine("The following permissions included in token are unnecessary:")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, permissionsInfo.ExcessPermissions.Select(p => $"- {p}"))
                    .AppendLine()
                : sb.AppendLine()
                    .AppendLine("The token has the minimal permissions required.");

            _ = sb.AppendLine();
        }

        if (DelegatedPermissions is not null)
        {
            transformPermissionsInfo(DelegatedPermissions, "delegated");
        }
        if (ApplicationPermissions is not null)
        {
            transformPermissionsInfo(ApplicationPermissions, "application");
        }

        if (ExcludedPermissions is not null &&
            ExcludedPermissions.Any())
        {
            _ = sb.AppendLine("## Excluded permissions")
                .AppendLine()
                .AppendJoin(Environment.NewLine, ExcludedPermissions.Select(p => $"- {p}"))
                .AppendLine();
        }

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        void transformPermissionsInfo(GraphMinimalPermissionsInfo permissionsInfo, string type)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{type} permissions for:")
                .AppendLine()
                .AppendLine(string.Join(Environment.NewLine, permissionsInfo.Operations.Select(o => $"- {o.Method} {o.Endpoint}")))
                .AppendLine()
                .AppendLine("Minimal permissions:")
                .AppendLine()
                .AppendLine(string.Join(", ", permissionsInfo.MinimalPermissions))
                .AppendLine()
                .AppendLine("Permissions on the token:")
                .AppendLine()
                .AppendLine(string.Join(", ", permissionsInfo.PermissionsFromTheToken));

            _ = permissionsInfo.ExcessPermissions.Any()
                ? sb.AppendLine()
                    .AppendLine("The following permissions are unnecessary:")
                    .AppendLine()
                    .AppendLine(string.Join(", ", permissionsInfo.ExcessPermissions))
                : sb.AppendLine()
                    .AppendLine("The token has the minimal permissions required.");

            _ = sb.AppendLine();
        }

        if (DelegatedPermissions is not null)
        {
            transformPermissionsInfo(DelegatedPermissions, "Delegated");
        }
        if (ApplicationPermissions is not null)
        {
            transformPermissionsInfo(ApplicationPermissions, "Application");
        }

        if (ExcludedPermissions is not null &&
            ExcludedPermissions.Any())
        {
            _ = sb.AppendLine("Excluded: permissions:")
                .AppendLine()
                .AppendLine(string.Join(", ", ExcludedPermissions));
        }

        return sb.ToString();
    }
}