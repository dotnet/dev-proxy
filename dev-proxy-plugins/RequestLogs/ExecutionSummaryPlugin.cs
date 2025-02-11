// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public abstract class ExecutionSummaryPluginReportBase
{
    public required Dictionary<string, Dictionary<string, Dictionary<string, int>>> Data { get; init; }
    public required IEnumerable<RequestLog> Logs { get; init; }
}

public class ExecutionSummaryPluginReportByUrl : ExecutionSummaryPluginReportBase;
public class ExecutionSummaryPluginReportByMessageType : ExecutionSummaryPluginReportBase;

internal enum SummaryGroupBy
{
    Url,
    MessageType
}

internal class ExecutionSummaryPluginConfiguration
{
    public SummaryGroupBy GroupBy { get; set; } = SummaryGroupBy.Url;
}

public class ExecutionSummaryPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : BaseReportingPlugin(pluginEvents, context, logger, urlsToWatch, configSection)
{
    public override string Name => nameof(ExecutionSummaryPlugin);
    private readonly ExecutionSummaryPluginConfiguration _configuration = new();
    private static readonly string _groupByOptionName = "--summary-group-by";
    private const string _requestsInterceptedMessage = "Requests intercepted";
    private const string _requestsPassedThroughMessage = "Requests passed through";

    public override Option[] GetOptions()
    {
        var groupBy = new Option<SummaryGroupBy?>(_groupByOptionName, "Specifies how the information should be grouped in the summary. Available options: `url` (default), `messageType`.")
        {
            ArgumentHelpName = "summary-group-by"
        };
        groupBy.AddValidator(input =>
        {
            if (!Enum.TryParse<SummaryGroupBy>(input.Tokens[0].Value, true, out var groupBy))
            {
                input.ErrorMessage = $"{input.Tokens[0].Value} is not a valid option to group by. Allowed values are: {string.Join(", ", Enum.GetNames(typeof(SummaryGroupBy)))}";
            }
        });

        return [groupBy];
    }

    public override async Task RegisterAsync()
    {
        await base.RegisterAsync();

        ConfigSection?.Bind(_configuration);

        PluginEvents.OptionsLoaded += OnOptionsLoaded;
        PluginEvents.AfterRecordingStop += AfterRecordingStopAsync;
    }

    private void OnOptionsLoaded(object? sender, OptionsLoadedArgs e)
    {
        InvocationContext context = e.Context;

        var groupBy = context.ParseResult.GetValueForOption<SummaryGroupBy?>(_groupByOptionName, e.Options);
        if (groupBy is not null)
        {
            _configuration.GroupBy = groupBy.Value;
        }
    }

    private Task AfterRecordingStopAsync(object? sender, RecordingArgs e)
    {
        if (!e.RequestLogs.Any())
        {
            Logger.LogRequest("No messages recorded", MessageType.Skipped);
            return Task.CompletedTask;
        }

        ExecutionSummaryPluginReportBase report = _configuration.GroupBy switch
        {
            SummaryGroupBy.Url => new ExecutionSummaryPluginReportByUrl { Data = GetGroupedByUrlData(e.RequestLogs), Logs = e.RequestLogs },
            SummaryGroupBy.MessageType => new ExecutionSummaryPluginReportByMessageType { Data = GetGroupedByMessageTypeData(e.RequestLogs), Logs = e.RequestLogs },
            _ => throw new NotImplementedException()
        };

        StoreReport(report, e);

        return Task.CompletedTask;
    }

    // in this method we're producing the follow data structure
    // request > message type > (count) message
    private Dictionary<string, Dictionary<string, Dictionary<string, int>>> GetGroupedByUrlData(IEnumerable<RequestLog> requestLogs)
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
            if (!data[methodAndUrl].TryGetValue(readableMessageType, out Dictionary<string, int>? value))
            {
                value = ([]);
                data[methodAndUrl].Add(readableMessageType, value);
            }

            if (value.TryGetValue(message, out int val))
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
    private Dictionary<string, Dictionary<string, Dictionary<string, int>>> GetGroupedByMessageTypeData(IEnumerable<RequestLog> requestLogs)
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
            if (!data.TryGetValue(readableMessageType, out Dictionary<string, Dictionary<string, int>>? value))
            {
                value = [];
                data.Add(readableMessageType, value);

                if (log.MessageType == MessageType.InterceptedRequest ||
                    log.MessageType == MessageType.PassedThrough)
                {
                    // intercepted and passed through requests don't have
                    // a sub-grouping so let's repeat the message type
                    // to keep the same data shape
                    data[readableMessageType].Add(readableMessageType, []);
                }
            }

            var message = GetRequestMessage(log);
            if (log.MessageType == MessageType.InterceptedRequest ||
                log.MessageType == MessageType.PassedThrough)
            {
                // for passed through requests we need to log the URL rather than the
                // fixed message
                if (log.MessageType == MessageType.PassedThrough)
                {
                    message = GetMethodAndUrl(log);
                }

                if (!value[readableMessageType].ContainsKey(message))
                {
                    value[readableMessageType].Add(message, 1);
                }
                else
                {
                    value[readableMessageType][message]++;
                }
                continue;
            }

            if (!value.TryGetValue(message, out Dictionary<string, int>? val))
            {
                val = ([]);
                value.Add(message, val);
            }
            var methodAndUrl = GetMethodAndUrl(log);
            if (value[message].ContainsKey(methodAndUrl))
            {
                value[message][methodAndUrl]++;
            }
            else
            {
                value[message].Add(methodAndUrl, 1);
            }
        }

        return data;
    }

    private static string GetRequestMessage(RequestLog requestLog)
    {
        return string.Join(' ', requestLog.Message);
    }

    private static string GetMethodAndUrl(RequestLog requestLog)
    {
        if (requestLog.Context is not null)
        {
            return $"{requestLog.Context.Session.HttpClient.Request.Method} {requestLog.Context.Session.HttpClient.Request.RequestUri}";
        }
        else
        {
            return "Undefined";
        }
    }

    private static string GetReadableMessageTypeForSummary(MessageType messageType) => messageType switch
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
