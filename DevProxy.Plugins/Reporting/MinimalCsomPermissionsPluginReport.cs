// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class MinimalCsomPermissionsPluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<string> Actions { get; init; }
    public required IEnumerable<string> Errors { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine($"# Minimal CSOM permissions report")
            .AppendLine()

            .AppendLine("## Actions")
            .AppendLine()
            .AppendJoin(Environment.NewLine, Actions.Select(a => $"- {a}"))
            .AppendLine()

            .AppendLine()
            .AppendLine("## Minimal permissions")
            .AppendLine()
            .AppendJoin(Environment.NewLine, MinimalPermissions.Select(p => $"- {p}"))
            .AppendLine();

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("## 🛑 Errors")
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following actions:")
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

        _ = sb.AppendLine($"Minimal CSOM permissions report")
            .AppendLine()
            .AppendLine("Actions:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, Actions.Select(a => $"- {a}"))
            .AppendLine()
            .AppendLine()
            .AppendLine("Minimal permissions:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, MinimalPermissions.Select(p => $"- {p}"));

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following actions:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors.Select(e => $"- {e}"));
        }

        return sb.ToString();
    }
}