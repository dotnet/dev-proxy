// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Logging;

sealed class MachineConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "devproxy-machine";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Mapping from MessageType to semantic type strings for machine output
    private static readonly Dictionary<MessageType, string> _messageTypeStrings = new()
    {
        [MessageType.InterceptedRequest] = "request",
        [MessageType.InterceptedResponse] = "response",
        [MessageType.PassedThrough] = "passthrough",
        [MessageType.Chaos] = "chaos",
        [MessageType.Warning] = "warning",
        [MessageType.Mocked] = "mock",
        [MessageType.Failed] = "error",
        [MessageType.Tip] = "tip",
        [MessageType.Skipped] = "skip",
        [MessageType.Processed] = "processed",
        [MessageType.Timestamp] = "timestamp",
        [MessageType.FinishedProcessingRequest] = "finished",
        [MessageType.Normal] = "info"
    };

    // Mapping from LogLevel to semantic level strings for machine output
    private static readonly Dictionary<LogLevel, string> _logLevelStrings = new()
    {
        [LogLevel.Trace] = "trace",
        [LogLevel.Debug] = "debug",
        [LogLevel.Information] = "info",
        [LogLevel.Warning] = "warning",
        [LogLevel.Error] = "error",
        [LogLevel.Critical] = "critical"
    };

    private readonly ProxyConsoleFormatterOptions _options;
    private readonly HashSet<MessageType> _filteredMessageTypes;

    public MachineConsoleFormatter(IOptions<ProxyConsoleFormatterOptions> options) : base(FormatterName)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _options = options.Value;

        _filteredMessageTypes = [MessageType.InterceptedResponse, MessageType.FinishedProcessingRequest];
        if (!_options.ShowSkipMessages)
        {
            _ = _filteredMessageTypes.Add(MessageType.Skipped);
        }
        if (!_options.ShowTimestamps)
        {
            _ = _filteredMessageTypes.Add(MessageType.Timestamp);
        }
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (logEntry.State is RequestLog requestLog)
        {
            WriteRequestLog(requestLog, logEntry.Category, scopeProvider, textWriter);
        }
        else
        {
            WriteRegularLogMessage(logEntry, scopeProvider, textWriter);
        }
    }

    private void WriteRequestLog(RequestLog requestLog, string category, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var messageType = requestLog.MessageType;

        if (_filteredMessageTypes.Contains(messageType))
        {
            return;
        }

        var requestId = GetRequestIdScope(scopeProvider);
        var pluginName = category == ProxyConsoleFormatter.DefaultCategoryName ? null : category;

        // Extract short plugin name from full category
        if (pluginName is not null)
        {
            pluginName = pluginName[(pluginName.LastIndexOf('.') + 1)..];
        }

        var logObject = new MachineRequestLogEntry
        {
            Type = GetMessageTypeString(messageType),
            Message = requestLog.Message,
            Method = requestLog.Method,
            Url = requestLog.Url,
            Plugin = pluginName,
            RequestId = requestId?.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(logObject, _jsonOptions);
        textWriter.WriteLine(json);
    }

    private static void WriteRegularLogMessage<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var requestId = GetRequestIdScope(scopeProvider);
        var category = logEntry.Category;

        // Extract short category name
        if (category is not null)
        {
            category = category[(category.LastIndexOf('.') + 1)..];
        }

        var logObject = new MachineLogEntry
        {
            Type = "log",
            Level = GetLogLevelString(logEntry.LogLevel),
            Message = message,
            Category = category,
            RequestId = requestId?.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Exception = logEntry.Exception?.ToString()
        };

        var json = JsonSerializer.Serialize(logObject, _jsonOptions);
        textWriter.WriteLine(json);
    }

    private static string GetMessageTypeString(MessageType messageType) =>
        _messageTypeStrings.TryGetValue(messageType, out var str) ? str : "unknown";

    private static string GetLogLevelString(LogLevel logLevel) =>
        _logLevelStrings.TryGetValue(logLevel, out var str) ? str : "unknown";

    private static int? GetRequestIdScope(IExternalScopeProvider? scopeProvider)
    {
        int? requestId = null;
        scopeProvider?.ForEachScope((scope, _) =>
        {
            if (scope is Dictionary<string, object> dictionary &&
                dictionary.TryGetValue(nameof(requestId), out var req))
            {
                requestId = (int)req;
            }
        }, "");
        return requestId;
    }

    // JSON serialization models for machine output
    private sealed class MachineRequestLogEntry
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
        public string? Method { get; set; }
        public string? Url { get; set; }
        public string? Plugin { get; set; }
        public string? RequestId { get; set; }
        public string? Timestamp { get; set; }
    }

    private sealed class MachineLogEntry
    {
        public string? Type { get; set; }
        public string? Level { get; set; }
        public string? Message { get; set; }
        public string? Category { get; set; }
        public string? RequestId { get; set; }
        public string? Timestamp { get; set; }
        public string? Exception { get; set; }
    }
}
