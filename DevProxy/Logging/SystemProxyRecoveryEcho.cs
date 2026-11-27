// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Commands;

namespace DevProxy.Logging;

internal static class SystemProxyRecoveryEcho
{
    // The daemon recovers a system proxy left by a crashed instance before it starts
    // listening, so its log has the outcome by now. Show it like a foreground start would:
    // every record from the first to the last recovery message, so that warnings logged
    // while restoring (like proxy credentials that need re-entering) aren't hidden.
    public static async Task WriteAsync(string? log, bool isJsonOutput, TextWriter stdout, TextWriter stderr)
    {
        if (string.IsNullOrEmpty(log))
        {
            return;
        }

        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var first = Array.FindIndex(lines, IsRecoveryLine);
        if (first < 0)
        {
            return;
        }
        var last = Array.FindLastIndex(lines, IsRecoveryLine);

        var toError = false;
        foreach (var line in lines[first..(last + 1)])
        {
            if (isJsonOutput)
            {
                await stdout.WriteLineAsync(line);
                continue;
            }

            // Exception details continue a record on the following lines, so they go to
            // the same stream as the record they belong to. Warnings and errors go to stderr,
            // like they do in the foreground.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("info ", StringComparison.Ordinal) ||
                trimmed.StartsWith("dbug ", StringComparison.Ordinal) ||
                trimmed.StartsWith("trce ", StringComparison.Ordinal))
            {
                toError = false;
            }
            else if (trimmed.StartsWith("warn ", StringComparison.Ordinal) ||
                trimmed.StartsWith("fail ", StringComparison.Ordinal) ||
                trimmed.StartsWith("crit ", StringComparison.Ordinal))
            {
                toError = true;
            }
            await (toError ? stderr : stdout).WriteLineAsync(line);
        }

        static bool IsRecoveryLine(string line) =>
            line.Contains(DevProxyCommand.CrashedInstanceRecoveryMarker, StringComparison.Ordinal);
    }
}
