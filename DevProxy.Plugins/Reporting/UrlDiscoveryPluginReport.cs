// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public sealed class UrlDiscoveryPluginReport : IJsonReport, IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<string> Data { get; init; }

    public string FileExtension => ".json";

    public object ToJson() => new Dictionary<string, IEnumerable<string>>
    {
        ["urlsToWatch"] = Data
    };

    public string? ToMarkdown()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("## Wildcards")
            .AppendLine("")
            .AppendLine("You can use wildcards to catch multiple URLs with the same pattern.")
            .AppendLine("For example, you can use the following URL pattern to catch all API requests to")
            .AppendLine("JSON Placeholder API:")
            .AppendLine("")
            .AppendLine("```text")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("```")
            .AppendLine("")
            .AppendLine("## Excluding URLs")
            .AppendLine("")
            .AppendLine("You can exclude URLs with ! to prevent them from being intercepted.")
            .AppendLine("For example, you can exclude the URL `https://jsonplaceholder.typicode.com/authors`")
            .AppendLine("by using the following URL pattern:")
            .AppendLine("")
            .AppendLine("```text")
            .AppendLine("!https://jsonplaceholder.typicode.com/authors")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("```")
            .AppendLine("")
            .AppendLine("Intercepted URLs:")
            .AppendLine()
            .AppendLine("```text")

            .AppendJoin(Environment.NewLine, Data)

            .AppendLine("")
            .AppendLine("```");
        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("Wildcards")
            .AppendLine("")
            .AppendLine("You can use wildcards to catch multiple URLs with the same pattern.")
            .AppendLine("For example, you can use the following URL pattern to catch all API requests to")
            .AppendLine("JSON Placeholder API:")
            .AppendLine("")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("")
            .AppendLine("Excluding URLs")
            .AppendLine("")
            .AppendLine("You can exclude URLs with ! to prevent them from being intercepted.")
            .AppendLine("For example, you can exclude the URL https://jsonplaceholder.typicode.com/authors")
            .AppendLine("by using the following URL pattern:")
            .AppendLine("")
            .AppendLine("!https://jsonplaceholder.typicode.com/authors")
            .AppendLine("https://jsonplaceholder.typicode.com/*")
            .AppendLine("")
            .AppendLine("Intercepted URLs:")
            .AppendLine()

            .AppendJoin(Environment.NewLine, Data);

        return sb.ToString();
    }
}