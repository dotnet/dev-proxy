// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Proxy;
using Microsoft.Extensions.Logging.Console;

namespace DevProxy.Logging;

sealed class ProxyConsoleFormatterOptions : ConsoleFormatterOptions
{
    public LogFor LogFor { get; set; } = LogFor.Human;

    public bool ShowSkipMessages { get; set; } = true;

    public bool ShowTimestamps { get; set; } = true;
}