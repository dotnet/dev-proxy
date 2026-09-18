// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging;

namespace DevProxy.Integration.Tests;

/// <summary>
/// An <see cref="ILoggerFactory"/> whose loggers capture every <see cref="RequestLog"/>
/// emitted via <c>ILogger.LogRequest(...)</c> — mirroring what the host's production
/// <c>RequestLogger</c> does (it enqueues <see cref="RequestLog"/> state objects).
///
/// <para>This is the seam that lets the in-process engine harness assert plugin
/// behaviour that surfaces as log/guidance entries rather than HTTP responses:</para>
///
/// <code>
///   traffic ─▶ KestrelProxyEngine (PluginPipeline) ──┐
///                                                      ├─▶ LogRequest(RequestLog)
///   guidance/behaviour plugin (its ILogger) ─────────┘          │
///                                                                ▼
///                                                   CapturingLogger.Logs (thread-safe)
///                                                                │
///                              reporter.AfterRecordingStopAsync(RecordingArgs(Logs))
/// </code>
///
/// Both the engine and any plugin constructed with a logger from this factory write
/// into the same sink, so a single <see cref="Logs"/> collection reflects the full run.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<RequestLog> _logs = new();
    private readonly CapturingLogger _logger;

    public CapturingLoggerFactory() => _logger = new CapturingLogger(_logs);

    /// <summary>Every <see cref="RequestLog"/> captured so far, in emission order.</summary>
    public IReadOnlyList<RequestLog> Logs => [.. _logs];

    /// <summary>
    /// Captured logs filtered to a single <see cref="MessageType"/> — convenience for
    /// asserting a plugin emitted (e.g.) a <see cref="MessageType.Warning"/> tip.
    /// </summary>
    public IReadOnlyList<RequestLog> LogsOfType(MessageType type) =>
        [.. _logs.Where(l => l.MessageType == type)];

    public ILogger CreateLogger(string categoryName) => _logger;

    public void AddProvider(ILoggerProvider provider)
    {
        // No-op: this factory only ever hands out the single capturing logger.
    }

    public void Dispose()
    {
        // Nothing to dispose; the backing queue is released with the instance.
    }

    private sealed class CapturingLogger(ConcurrentQueue<RequestLog> sink) : ILogger
    {
        private readonly ConcurrentQueue<RequestLog> _sink = sink;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is RequestLog requestLog)
            {
                _sink.Enqueue(requestLog);
            }
        }
    }
}
