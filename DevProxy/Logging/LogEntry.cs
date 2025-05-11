// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging.Abstractions;

namespace DevProxy.Logging;

readonly struct LogEntry
{
    public LogLevel LogLevel { get; init; }
    public string Category { get; init; }
    public EventId EventId { get; init; }
    public string? Message { get; init; }
    public Exception? Exception { get; init; }

    public static LogEntry FromLogEntry<TState>(LogEntry<TState> logEntry)
    {
        return new()
        {
            LogLevel = logEntry.LogLevel,
            Category = logEntry.Category,
            EventId = logEntry.EventId,
            Message = logEntry.Formatter(logEntry.State, logEntry.Exception),
            Exception = logEntry.Exception
        };
    }
}