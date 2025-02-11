// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging.Console;

namespace Microsoft.DevProxy.Logging;

public class ProxyConsoleFormatterOptions: ConsoleFormatterOptions
{
    public bool ShowSkipMessages { get; set; } = true;

    public bool ShowTimestamps { get; set; } = true;
}