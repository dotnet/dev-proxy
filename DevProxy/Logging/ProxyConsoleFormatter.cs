// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using DevProxy.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;

namespace DevProxy.Logging;

sealed class ProxyConsoleFormatter : ConsoleFormatter
{
    public const string DefaultCategoryName = "devproxy";

    private const string _boxTopLeft = "\u256d ";
    private const string _boxLeft = "\u2502 ";
    private const string _boxBottomLeft = "\u2570 ";
    private const string _boxSpacing = "  ";
    private const string _labelSpacing = " ";

    // Cached lookup tables for better performance
    private static readonly Dictionary<LogLevel, string> _logLevelStrings = new()
    {
        [LogLevel.Trace] = "trce",
        [LogLevel.Debug] = "dbug",
        [LogLevel.Information] = "info",
        [LogLevel.Warning] = "warn",
        [LogLevel.Error] = "fail",
        [LogLevel.Critical] = "crit"
    };
    private static readonly Dictionary<LogLevel, (ConsoleColor bg, ConsoleColor fg)> _logLevelColors = new()
    {
        [LogLevel.Information] = (Console.BackgroundColor, ConsoleColor.Blue),
        [LogLevel.Warning] = (ConsoleColor.DarkYellow, Console.ForegroundColor),
        [LogLevel.Error] = (ConsoleColor.DarkRed, Console.ForegroundColor),
        [LogLevel.Debug] = (Console.BackgroundColor, ConsoleColor.Gray),
        [LogLevel.Trace] = (Console.BackgroundColor, ConsoleColor.Gray)
    };
    private static readonly Dictionary<MessageType, string> _messageTypeStrings = new()
    {
        [MessageType.InterceptedRequest] = "req",
        [MessageType.InterceptedResponse] = "res",
        [MessageType.PassedThrough] = "pass",
        [MessageType.Chaos] = "oops",
        [MessageType.Warning] = "warn",
        [MessageType.Mocked] = "mock",
        [MessageType.Failed] = "fail",
        [MessageType.Tip] = "tip",
        [MessageType.Skipped] = "skip",
        [MessageType.Processed] = "proc",
        [MessageType.Timestamp] = "time",
        [MessageType.FinishedProcessingRequest] = "    "
    };
    private static readonly Dictionary<MessageType, (ConsoleColor bg, ConsoleColor fg)> _messageTypeColors = new()
    {
        [MessageType.InterceptedRequest] = (Console.BackgroundColor, ConsoleColor.Gray),
        [MessageType.PassedThrough] = (ConsoleColor.Gray, Console.ForegroundColor),
        [MessageType.Skipped] = (Console.BackgroundColor, ConsoleColor.Gray),
        [MessageType.Processed] = (ConsoleColor.DarkGreen, Console.ForegroundColor),
        [MessageType.Chaos] = (ConsoleColor.DarkRed, Console.ForegroundColor),
        [MessageType.Warning] = (ConsoleColor.DarkYellow, Console.ForegroundColor),
        [MessageType.Mocked] = (ConsoleColor.DarkMagenta, Console.ForegroundColor),
        [MessageType.Failed] = (ConsoleColor.DarkRed, Console.ForegroundColor),
        [MessageType.Tip] = (ConsoleColor.DarkBlue, Console.ForegroundColor),
        [MessageType.Timestamp] = (Console.BackgroundColor, ConsoleColor.Gray)
    };

    private readonly ConcurrentDictionary<int, List<object>> _messages = [];
    private readonly ProxyConsoleFormatterOptions _options;
    private readonly HashSet<MessageType> _filteredMessageTypes;

    public ProxyConsoleFormatter(IOptions<ProxyConsoleFormatterOptions> options) : base(DefaultCategoryName)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _options = options.Value;

        _filteredMessageTypes = [MessageType.InterceptedResponse];
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
            LogRequest(requestLog, logEntry.Category, scopeProvider, textWriter);
        }
        else
        {
            LogRegularLogMessage(logEntry, scopeProvider, textWriter);
        }
    }

    private void LogRequest(RequestLog requestLog, string category, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var messageType = requestLog.MessageType;

        if (_filteredMessageTypes.Contains(messageType))
        {
            return;
        }

        var requestId = GetRequestIdScope(scopeProvider);
        if (requestId is null)
        {
            return;
        }

        if (messageType == MessageType.FinishedProcessingRequest)
        {
            FlushLogsForRequest(requestId.Value, textWriter);
        }
        else
        {
            BufferRequestLog(requestLog, category, requestId.Value);
        }
    }

    private void FlushLogsForRequest(int requestId, TextWriter textWriter)
    {
        if (!_messages.TryGetValue(requestId, out var messages))
        {
            return;
        }

        var lastIndex = messages.Count - 1;
        for (var i = 0; i < messages.Count; i++)
        {
            var isLast = i == lastIndex;
            switch (messages[i])
            {
                case RequestLog log:
                    WriteRequestLogMessage(log, textWriter, isLast);
                    break;
                case LogEntry logEntry:
                    WriteRegularLogMessage(logEntry, textWriter, isLast);
                    break;
                default:
                    // noop
                    break;
            }
        }
        _ = _messages.TryRemove(requestId, out _);
    }

    private void BufferRequestLog(RequestLog requestLog, string category, int requestId)
    {
        requestLog.PluginName = category == DefaultCategoryName ? null : category;
        var messages = _messages.GetOrAdd(requestId, _ => []);
        messages.Add(requestLog);
    }

    private void LogRegularLogMessage<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var requestId = GetRequestIdScope(scopeProvider);
        if (requestId is null)
        {
            WriteRegularLogMessage(logEntry, scopeProvider, textWriter);
        }
        else
        {
            var message = LogEntry.FromLogEntry(logEntry);
            var messages = _messages.GetOrAdd(requestId.Value, _ => []);
            messages.Add(message);
        }
    }

    private void WriteRegularLogMessage<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        WriteMessageWithLabel(message, logEntry.LogLevel, scopeProvider, textWriter);

        if (logEntry.Exception is not null)
        {
            textWriter.Write($" Exception Details: {logEntry.Exception}");
        }
        textWriter.WriteLine();
    }

    private void WriteMessageWithLabel(string? message, LogLevel logLevel, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (message is null)
        {
            return;
        }

        var label = GetLogLevelString(logLevel);
        var (bgColor, fgColor) = GetLogLevelColor(logLevel);

        WriteLabel(textWriter, label, bgColor, fgColor);
        textWriter.Write($"{_labelSpacing}{_boxSpacing}");

        if (logLevel == LogLevel.Debug)
        {
            textWriter.Write($"[{DateTime.Now:T}] ");
        }

        WriteScopes(scopeProvider, textWriter);
        textWriter.Write(message);
    }

    private void WriteRequestLogMessage(RequestLog log, TextWriter textWriter, bool isLast)
    {
        var label = GetMessageTypeString(log.MessageType);
        var (bgColor, fgColor) = GetMessageTypeColor(log.MessageType);
        var boxChar = GetBoxCharacter(log.MessageType, isLast);

        if (log.MessageType == MessageType.InterceptedRequest)
        {
            textWriter.WriteLine();
        }

        WriteLabel(textWriter, label, bgColor, fgColor);
        textWriter.Write($"{GetLabelPadding(label)}{_labelSpacing}{boxChar}");
        WritePluginName(textWriter, log.PluginName);
        textWriter.WriteLine(log.Message);
    }

    private void WriteRegularLogMessage(LogEntry log, TextWriter textWriter, bool isLast)
    {
        var label = GetLogLevelString(log.LogLevel);
        var (bgColor, fgColor) = GetLogLevelColor(log.LogLevel);
        var boxChar = isLast ? _boxBottomLeft : _boxLeft;

        WriteLabel(textWriter, label, bgColor, fgColor);
        textWriter.Write($"{GetLabelPadding(label)}{_labelSpacing}{boxChar}");
        WritePluginName(textWriter, log.Category);
        textWriter.WriteLine(log.Message);
    }

    private void WritePluginName(TextWriter textWriter, string? categoryOrPluginName)
    {
        if (!_options.IncludeScopes || categoryOrPluginName is null)
        {
            return;
        }

        var pluginName = categoryOrPluginName[(categoryOrPluginName.LastIndexOf('.') + 1)..];
        if (pluginName != nameof(ProxyEngine))
        {
            textWriter.Write($"{pluginName}: ");
        }
    }

    private void WriteScopes(IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (!_options.IncludeScopes || scopeProvider is null)
        {
            return;
        }

        scopeProvider.ForEachScope((scope, state) =>
        {
            if (scope is null)
            {
                return;
            }

            var scopeText = scope is string scopeString ? scopeString :
                          scope.GetType().Name == "FormattedLogValues" ? scope.ToString() : null;

            if (scopeText is not null)
            {
                textWriter.Write($"{scopeText}: ");
            }
        }, textWriter);
    }

    private static string GetLogLevelString(LogLevel logLevel) =>
        _logLevelStrings.TryGetValue(logLevel, out var str) ? str : throw new ArgumentOutOfRangeException(nameof(logLevel));

    private static (ConsoleColor bg, ConsoleColor fg) GetLogLevelColor(LogLevel logLevel) =>
        _logLevelColors.TryGetValue(logLevel, out var color) ? color : (Console.BackgroundColor, Console.ForegroundColor);

    private static string GetMessageTypeString(MessageType messageType) =>
        _messageTypeStrings.TryGetValue(messageType, out var str) ? str : "    ";

    private static (ConsoleColor bg, ConsoleColor fg) GetMessageTypeColor(MessageType messageType) =>
        _messageTypeColors.TryGetValue(messageType, out var color) ? color : (Console.BackgroundColor, Console.ForegroundColor);

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

    private static void WriteLabel(TextWriter textWriter, string label, ConsoleColor bgColor, ConsoleColor fgColor)
    {
        textWriter.WriteColoredMessage($" {label} ", bgColor, fgColor);
    }

    private static string GetLabelPadding(string label) =>
        label.Length < 4 ? " " : "";

    private static string GetBoxCharacter(MessageType messageType, bool isLast) =>
        messageType == MessageType.InterceptedRequest ? _boxTopLeft :
        isLast ? _boxBottomLeft : _boxLeft;
}