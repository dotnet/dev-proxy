// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// A single HTTP/1.x message head parsed off the wire: request line plus headers.
/// </summary>
/// <param name="Method">Request method token (e.g. <c>GET</c>, <c>CONNECT</c>).</param>
/// <param name="Target">Request target (origin-form path, absolute-form URL, or CONNECT authority).</param>
/// <param name="Version">HTTP version token (e.g. <c>HTTP/1.1</c>).</param>
/// <param name="Headers">Headers in wire order (names/values trimmed).</param>
internal sealed record ParsedRequestHead(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<(string Name, string Value)> Headers);

/// <summary>
/// Stateless HTTP/1.x parsing helpers shared by <see cref="Http1ConnectionReader"/>.
/// Kept separate so the byte-level framing rules have one implementation and one
/// test surface, independent of any particular stream/connection.
/// </summary>
internal static class Http1RequestReader
{
    public const int MaxHeaderBlockBytes = 1024 * 1024;

    /// <summary>Parses a CRLF-delimited header block (request line + header lines).</summary>
    /// <exception cref="InvalidOperationException">The request line is malformed.</exception>
    public static ParsedRequestHead ParseHead(string headerText)
    {
        ArgumentNullException.ThrowIfNull(headerText);

        var lines = headerText.Split("\r\n");
        var startLine = lines[0].Split(' ', 3);
        if (startLine.Length < 3)
        {
            throw new InvalidOperationException($"Malformed HTTP request line: '{lines[0]}'.");
        }

        var headers = new List<(string Name, string Value)>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            headers.Add((lines[i][..separator].Trim(), lines[i][(separator + 1)..].Trim()));
        }

        return new ParsedRequestHead(startLine[0], startLine[1], startLine[2], headers);
    }

    /// <summary>Reads the <c>Content-Length</c> header value, defaulting to 0.</summary>
    public static int GetContentLength(IReadOnlyList<(string Name, string Value)> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return 0;
    }

    /// <summary>Index of the first <c>CRLFCRLF</c> in <paramref name="data"/>, or -1.</summary>
    public static int IndexOfDoubleCrlf(IReadOnlyList<byte> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n'
                && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }
}
