// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Text;

namespace DevProxy.Plugins.Generation;

public sealed class HttpFileGeneratorPluginReport :
    List<string>, IMarkdownReport, IPlainTextReport
{
    public HttpFileGeneratorPluginReport() : base() { }

    public HttpFileGeneratorPluginReport(IEnumerable<string> collection) : base(collection) { }

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# Generated HTTP files")
            .AppendLine()
            .AppendJoin(Environment.NewLine, $"- {this}")
            .AppendLine()
            .AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("Generated HTTP files:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, this);

        return sb.ToString();
    }
}