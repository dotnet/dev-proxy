// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace DevProxy.Plugins.Reporting;

public enum SummaryGroupBy
{
    Url,
    MessageType
}

public sealed class ExecutionSummaryPluginConfiguration
{
    public SummaryGroupBy GroupBy { get; set; } = SummaryGroupBy.Url;
}

public sealed class ExecutionSummaryPlugin(
    ILogger<ExecutionSummaryPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<ExecutionSummaryPluginConfiguration>(
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    private const string _groupByOptionName = "--summary-group-by";
    private const string _requestsInterceptedMessage = "Requests intercepted";
    private const string _requestsPassedThroughMessage = "Requests passed through";

    public override string Name => nameof(ExecutionSummaryPlugin);

    public override Option[] GetOptions()
    {
        var groupBy = new Option<SummaryGroupBy?>(_groupByOptionName)
        {
            Description = "Specifies how the information should be grouped in the summary. Available options: `url` (default), `messageType`.",
            HelpName = "summary-group-by"
        };
        groupBy.Validators.Add(input =>
        {
            if (!Enum.TryParse<SummaryGroupBy>(input.Tokens[0].Value, true, out var groupBy))
            {
                input.AddError($"{input.Tokens[0].Value} is not a valid option to group by. Allowed values are: {string.Join(", ", Enum.GetNames<SummaryGroupBy>())}");
            }
        });

        return [groupBy];
    }

    public override void OptionsLoaded(OptionsLoadedArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OptionsLoaded(e);

        var parseResult = e.ParseResult;

        var groupBy = parseResult.GetValueOrDefault<SummaryGroupBy?>(_groupByOptionName);
        if (groupBy is not null)
        {
            Configuration.GroupBy = groupBy.Value;
        }
    }

    public override Task AfterRecordingStopAsync(RecordingArgs e)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        if (!e.RequestLogs.Any())
        {
            Logger.LogRequest("No messages recorded", MessageType.Skipped);
            return Task.CompletedTask;
        }

        var interceptedRequests = e.RequestLogs
            .Where(
                l => l.MessageType == MessageType.InterceptedRequest &&
                l.Context?.Session is not null &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context.Session.HttpClient.Request.RequestUri.AbsoluteUri)
            );

        ExecutionSummaryPluginReportBase report = Configuration.GroupBy switch
        {
            SummaryGroupBy.Url => new ExecutionSummaryPluginReportByUrl { Data = GetGroupedByUrlData(e.RequestLogs), Logs = interceptedRequests },
            SummaryGroupBy.MessageType => new ExecutionSummaryPluginReportByMessageType { Data = GetGroupedByMessageTypeData(e.RequestLogs), Logs = interceptedRequests },
            _ => throw new NotImplementedException()
        };

        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
        return Task.CompletedTask;
    }

    // in this method we're producing the follow data structure
    // request > message type > (count) message
    private static Dictionary<string, Dictionary<string, Dictionary<string, int>>> GetGroupedByUrlData(IEnumerable<RequestLog> requestLogs)
    {
        var data = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();

        foreach (var log in requestLogs)
        {
            var message = GetRequestMessage(log);
            if (log.MessageType == MessageType.InterceptedResponse)
            {
                // ignore intercepted response messages
                continue;
            }

            if (log.MessageType == MessageType.InterceptedRequest)
            {
                var request = GetMethodAndUrl(log);
                if (!data.ContainsKey(request))
                {
                    data.Add(request, []);
                }

                continue;
            }

            // last line of the message is the method and URL of the request
            var methodAndUrl = GetMethodAndUrl(log);
            var readableMessageType = GetReadableMessageTypeForSummary(log.MessageType);
            if (!data[methodAndUrl].TryGetValue(readableMessageType, out var value))
            {
                value = [];
                data[methodAndUrl].Add(readableMessageType, value);
            }

            if (value.TryGetValue(message, out var val))
            {
                value[message] = ++val;
            }
            else
            {
                value.Add(message, 1);
            }
        }

        return data;
    }

    // in this method we're producing the follow data structure
    // message type > message > (count) request
    private static Dictionary<string, Dictionary<string, Dictionary<string, int>>> GetGroupedByMessageTypeData(IEnumerable<RequestLog> requestLogs)
    {
        var data = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();

        foreach (var log in requestLogs)
        {
            if (log.MessageType == MessageType.InterceptedResponse)
            {
                // ignore intercepted response messages
                continue;
            }

            var readableMessageType = GetReadableMessageTypeForSummary(log.MessageType);
            if (!data.TryGetValue(readableMessageType, out var value))
            {
                value = [];
                data.Add(readableMessageType, value);

                if (log.MessageType is MessageType.InterceptedRequest or
                    MessageType.PassedThrough)
                {
                    // intercepted and passed through requests don't have
                    // a sub-grouping so let's repeat the message type
                    // to keep the same data shape
                    data[readableMessageType].Add(readableMessageType, []);
                }
            }

            var message = GetRequestMessage(log);
            if (log.MessageType is MessageType.InterceptedRequest or
                MessageType.PassedThrough)
            {
                // for passed through requests we need to log the URL rather than the
                // fixed message
                if (log.MessageType == MessageType.PassedThrough)
                {
                    message = GetMethodAndUrl(log);
                }

                if (!value[readableMessageType].TryGetValue(message, out var count))
                {
                    value[readableMessageType].Add(message, 1);
                }
                else
                {
                    value[readableMessageType][message] = ++count;
                }
                continue;
            }

            if (!value.TryGetValue(message, out _))
            {
                value.Add(message, []);
            }
            var methodAndUrl = GetMethodAndUrl(log);
            if (value[message].TryGetValue(methodAndUrl, out var val))
            {
                value[message][methodAndUrl] = ++val;
            }
            else
            {
                value[message].Add(methodAndUrl, 1);
            }
        }

        return data;
    }

    private static string GetRequestMessage(RequestLog requestLog) =>
        string.Join(' ', requestLog.Message);

    private static string GetMethodAndUrl(RequestLog requestLog)
    {
        return requestLog.Context is not null
            ? $"{requestLog.Context.Session.HttpClient.Request.Method} {requestLog.Context.Session.HttpClient.Request.RequestUri}"
            : "Undefined";
    }

#pragma warning disable IDE0072
    private static string GetReadableMessageTypeForSummary(MessageType messageType) => messageType switch
#pragma warning restore IDE0072
    {
        MessageType.Chaos => "Requests with chaos",
        MessageType.Failed => "Failures",
        MessageType.InterceptedRequest => _requestsInterceptedMessage,
        MessageType.Mocked => "Requests mocked",
        MessageType.PassedThrough => _requestsPassedThroughMessage,
        MessageType.Tip => "Tips",
        MessageType.Warning => "Warnings",
        _ => "Unknown"
    };
}
