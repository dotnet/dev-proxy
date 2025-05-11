// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Plugins.Models;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace DevProxy.Plugins.Reporting;

public sealed class GraphMinimalPermissionsPluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<string> Errors { get; init; }
    public required IEnumerable<string> MinimalPermissions { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GraphPermissionsType PermissionsType { get; init; }
    public required IEnumerable<GraphRequestInfo> Requests { get; init; }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"# Minimal {PermissionsType.ToString().ToLowerInvariant()} permissions report")
            .AppendLine();

        _ = sb.AppendLine("## Requests")
            .AppendLine()
            .AppendJoin(Environment.NewLine, Requests.Select(r => $"- {r.Method} {r.Url}"))
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

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Minimal {PermissionsType.ToString().ToLowerInvariant()} permissions report")
            .AppendLine()
            .AppendLine("Requests:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, Requests.Select(r => $"- {r.Method} {r.Url}"))
            .AppendLine()
            .AppendLine()
            .AppendLine("Minimal permissions:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, MinimalPermissions.Select(p => $"- {p}"));

        if (Errors.Any())
        {
            _ = sb.AppendLine()
                .AppendLine("Couldn't determine minimal permissions for the following URLs:")
                .AppendLine()
                .AppendJoin(Environment.NewLine, Errors.Select(e => $"- {e}"));
        }

        return sb.ToString();
    }
}