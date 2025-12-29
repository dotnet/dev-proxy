// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace DevProxy.Logging;

internal sealed class StdioFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, StdioFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private bool _disposed;

    public StdioFileLoggerProvider(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new StdioFileLogger(name, this));
    }

    internal void WriteLog(string categoryName, LogLevel logLevel, string message)
    {
        if (_disposed)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var level = logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };

        using (_lock.EnterScope())
        {
            _writer.WriteLine($"[{timestamp}] {level}: {categoryName}");
            _writer.WriteLine($"      {message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loggers.Clear();
        _writer.Dispose();
    }
}

internal sealed class StdioFileLogger(string categoryName, StdioFileLoggerProvider provider) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly StdioFileLoggerProvider _provider = provider;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception;
        }

        _provider.WriteLog(_categoryName, logLevel, message);
    }
}
