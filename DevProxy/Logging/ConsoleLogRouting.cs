// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DevProxy.Logging;

/// <summary>
/// Lets a command that writes its result to stdout (like <c>cert export</c>) move all
/// console diagnostics to stderr once System.CommandLine has selected it. Logging is
/// configured before plugins register their options and commands, so the command can't
/// be reliably identified from the raw arguments at that point.
/// </summary>
sealed class ConsoleLogRouting(bool allToStandardError = false) : IOptionsChangeTokenSource<ConsoleLoggerOptions>, IDisposable
{
    private CancellationTokenSource _changeTokenSource = new();

    public bool AllToStandardError { get; private set; } = allToStandardError;

    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken() => new CancellationChangeToken(_changeTokenSource.Token);

    public LogLevel GetStandardErrorThreshold(LogLevel defaultThreshold) =>
        AllToStandardError ? LogLevel.Trace : defaultThreshold;

    public void RouteAllToStandardError()
    {
        if (AllToStandardError)
        {
            return;
        }

        AllToStandardError = true;
        // Rebuilds ConsoleLoggerOptions, which updates the threshold of existing console loggers
        var previous = Interlocked.Exchange(ref _changeTokenSource, new CancellationTokenSource());
        previous.Cancel();
    }

    public void Dispose() => _changeTokenSource.Dispose();
}
