// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Reporting;

public abstract class ExecutionSummaryPluginReportBase : IJsonReport
{
    public required Dictionary<string, Dictionary<string, Dictionary<string, int>>> Data { get; init; }
    public required IEnumerable<RequestLog> Logs { get; init; }

    protected const string RequestsInterceptedMessage = "Requests intercepted";
    protected const string RequestsPassedThroughMessage = "Requests passed through";

    public string FileExtension => ".json";

    public object ToJson() => Data;

    protected static void AddExecutionSummaryReportSummary(IEnumerable<RequestLog> requestLogs, StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);

        static string getReadableMessageTypeForSummary(MessageType messageType)
        {
#pragma warning disable IDE0072
            return messageType switch
#pragma warning restore IDE0072
            {
                MessageType.Chaos => "Requests with chaos",
                MessageType.Failed => "Failures",
                MessageType.InterceptedRequest => RequestsInterceptedMessage,
                MessageType.Mocked => "Requests mocked",
                MessageType.PassedThrough => RequestsPassedThroughMessage,
                MessageType.Tip => "Tips",
                MessageType.Warning => "Warnings",
                _ => "Unknown"
            };
        }

        var data = requestLogs
          .Where(log => log.MessageType != MessageType.InterceptedResponse)
          .Select(log => getReadableMessageTypeForSummary(log.MessageType))
          .OrderBy(log => log)
          .GroupBy(log => log)
          .ToDictionary(group => group.Key, group => group.Count());

        _ = sb.AppendLine()
            .AppendLine("## Summary")
            .AppendLine()
            .AppendLine("Category|Count")
            .AppendLine("--------|----:");

        foreach (var messageType in data.Keys)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{messageType}|{data[messageType]}");
        }
    }
}

public sealed class ExecutionSummaryPluginReportByMessageType :
    ExecutionSummaryPluginReportBase, IMarkdownReport, IPlainTextReport
{
    public string? ToMarkdown()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# Dev Proxy execution summary")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}")
            .AppendLine();

        _ = sb.AppendLine("## Message types");

        var data = Data;
        var sortedMessageTypes = data.Keys.OrderBy(k => k);
        foreach (var messageType in sortedMessageTypes)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"### {messageType}");

            if (messageType is RequestsInterceptedMessage or
                RequestsPassedThroughMessage)
            {
                _ = sb.AppendLine();

                var sortedMethodAndUrls = data[messageType][messageType].Keys.OrderBy(k => k);
                foreach (var methodAndUrl in sortedMethodAndUrls)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({data[messageType][messageType][methodAndUrl]}) {methodAndUrl}");
                }
            }
            else
            {
                var sortedMessages = data[messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine()
                        .AppendLine(CultureInfo.InvariantCulture, $"#### {message}")
                        .AppendLine();

                    var sortedMethodAndUrls = data[messageType][message].Keys.OrderBy(k => k);
                    foreach (var methodAndUrl in sortedMethodAndUrls)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({data[messageType][message][methodAndUrl]}) {methodAndUrl}");
                    }
                }
            }
        }

        AddExecutionSummaryReportSummary(Logs, sb);
        _ = sb.AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("Dev Proxy execution summary")
            .AppendLine(CultureInfo.InvariantCulture, $"({DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)})")
            .AppendLine()

            .AppendLine(":: Message types".ToUpperInvariant());

        var sortedMessageTypes = Data.Keys.OrderBy(k => k);
        foreach (var messageType in sortedMessageTypes)
        {
            _ = sb.AppendLine()
                .AppendLine(messageType.ToUpperInvariant());

            if (messageType is RequestsInterceptedMessage or
                RequestsPassedThroughMessage)
            {
                _ = sb.AppendLine();

                var sortedMethodAndUrls = Data[messageType][messageType].Keys.OrderBy(k => k);
                foreach (var methodAndUrl in sortedMethodAndUrls)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({Data[messageType][messageType][methodAndUrl]}) {methodAndUrl}");
                }
            }
            else
            {
                var sortedMessages = Data[messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine()
                        .AppendLine(message)
                        .AppendLine();

                    var sortedMethodAndUrls = Data[messageType][message].Keys.OrderBy(k => k);
                    foreach (var methodAndUrl in sortedMethodAndUrls)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({Data[messageType][message][methodAndUrl]}) {methodAndUrl}");
                    }
                }
            }
        }

        AddExecutionSummaryReportSummary(Logs, sb);

        return sb.ToString();
    }
}

public sealed class ExecutionSummaryPluginReportByUrl :
    ExecutionSummaryPluginReportBase, IMarkdownReport, IPlainTextReport
{
    public string? ToMarkdown()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("# Dev Proxy execution summary")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}")
            .AppendLine();

        _ = sb.AppendLine("## Requests");

        var sortedMethodAndUrls = Data.Keys.OrderBy(k => k);
        foreach (var methodAndUrl in sortedMethodAndUrls)
        {
            _ = sb.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"### {methodAndUrl}");

            var sortedMessageTypes = Data[methodAndUrl].Keys.OrderBy(k => k);
            foreach (var messageType in sortedMessageTypes)
            {
                _ = sb.AppendLine()
                    .AppendLine(CultureInfo.InvariantCulture, $"#### {messageType}")
                    .AppendLine();

                var sortedMessages = Data[methodAndUrl][messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({Data[methodAndUrl][messageType][message]}) {message}");
                }
            }
        }

        AddExecutionSummaryReportSummary(Logs, sb);
        _ = sb.AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var sb = new StringBuilder();

        _ = sb.AppendLine("Dev Proxy execution summary")
            .AppendLine(CultureInfo.InvariantCulture, $"({DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)})")
            .AppendLine()

            .AppendLine(":: Requests".ToUpperInvariant());

        var sortedMethodAndUrls = Data.Keys.OrderBy(k => k);
        foreach (var methodAndUrl in sortedMethodAndUrls)
        {
            _ = sb.AppendLine()
                .AppendLine(methodAndUrl);

            var sortedMessageTypes = Data[methodAndUrl].Keys.OrderBy(k => k);
            foreach (var messageType in sortedMessageTypes)
            {
                _ = sb.AppendLine()
                    .AppendLine(messageType.ToUpperInvariant())
                    .AppendLine();

                var sortedMessages = Data[methodAndUrl][messageType].Keys.OrderBy(k => k);
                foreach (var message in sortedMessages)
                {
                    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- ({Data[methodAndUrl][messageType][message]}) {message}");
                }
            }
        }

        AddExecutionSummaryReportSummary(Logs, sb);

        return sb.ToString();
    }
}