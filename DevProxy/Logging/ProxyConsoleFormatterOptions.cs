// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DevProxy.Logging;

sealed class ProxyConsoleFormatterOptions : ConsoleFormatterOptions
{
    public bool ShowSkipMessages { get; set; } = true;

    public bool ShowTimestamps { get; set; } = true;

    public bool IncludeFullTimestamps { get; set; }

    // Minimum level for diagnostic messages. Structured output (command
    // results) is always written regardless of this level.
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Trace;
}