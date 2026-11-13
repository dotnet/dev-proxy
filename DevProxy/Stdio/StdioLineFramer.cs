// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Stdio;

/// <summary>
/// Splits a byte stream into newline-delimited messages (as used by JSON-RPC
/// over stdio, e.g. MCP). Partial lines are buffered until the terminating
/// newline arrives or the stream ends.
/// </summary>
internal sealed class StdioLineFramer
{
    private readonly List<byte> _pending = [];

    /// <summary>
    /// Appends data and returns all complete messages. Each message includes
    /// its trailing newline so that forwarded bytes are unchanged.
    /// </summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        var messages = new List<byte[]>();

        while (!data.IsEmpty)
        {
            var newlineIndex = data.IndexOf((byte)'\n');
            if (newlineIndex < 0)
            {
                _pending.AddRange(data);
                break;
            }

            var line = data[..(newlineIndex + 1)];
            byte[] message;
            if (_pending.Count == 0)
            {
                message = line.ToArray();
            }
            else
            {
                _pending.AddRange(line);
                message = [.. _pending];
                _pending.Clear();
            }

            messages.Add(message);
            data = data[(newlineIndex + 1)..];
        }

        return messages;
    }

    /// <summary>
    /// Returns any buffered data that wasn't terminated by a newline, or
    /// <c>null</c> if there is none.
    /// </summary>
    public byte[]? Flush()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        byte[] remainder = [.. _pending];
        _pending.Clear();
        return remainder;
    }
}
