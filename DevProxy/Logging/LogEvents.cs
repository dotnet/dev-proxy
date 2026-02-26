// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Logging;

static class LogEvents
{
    /// <summary>
    /// EventId for structured output (e.g., JSON from subcommands).
    /// When JsonConsoleFormatter sees this EventId, it wraps the message
    /// in a consistent envelope with type "result" and a data field
    /// containing the parsed JSON object.
    /// </summary>
    public static readonly EventId StructuredOutput = new(1, "StructuredOutput");
}
