// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Text;

namespace DevProxy.Plugins.Generation;

public sealed class OpenApiSpecGeneratorPluginReportItem
{
    public required string FileName { get; init; }
    public required string ServerUrl { get; init; }
}

public sealed class OpenApiSpecGeneratorPluginReport :
    List<OpenApiSpecGeneratorPluginReportItem>, IMarkdownReport, IPlainTextReport
{
    public OpenApiSpecGeneratorPluginReport() : base() { }

    public OpenApiSpecGeneratorPluginReport(IEnumerable<OpenApiSpecGeneratorPluginReportItem> collection) : base(collection) { }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# Generated OpenAPI specs")
            .AppendLine()
            .AppendLine("Server URL|File name")
            .AppendLine("---|---------")
            .AppendJoin(Environment.NewLine, this.Select(r => $"{r.ServerUrl}|{r.FileName}"))
            .AppendLine()
            .AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("Generated OpenAPI specs:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, this.Select(i => $"- {i.FileName} ({i.ServerUrl})"));

        return sb.ToString();
    }
}