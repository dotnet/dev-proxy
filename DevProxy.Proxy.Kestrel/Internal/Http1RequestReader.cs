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
/// How a request body is framed on the wire, resolved from the request headers.
///
/// <code>
///   Transfer-Encoding: chunked  AND  Content-Length  ──► Conflicting (smuggling risk)
///   Transfer-Encoding: chunked  (only)               ──► Chunked
///   Content-Length              (only)               ──► ContentLength
///   neither                                          ──► None
/// </code>
/// </summary>
internal enum RequestBodyFraming
{
    /// <summary>No body framing headers — no request body to read.</summary>
    None,

    /// <summary>A <c>Content-Length</c>-delimited body.</summary>
    ContentLength,

    /// <summary>A <c>Transfer-Encoding: chunked</c> body.</summary>
    Chunked,

    /// <summary>
    /// Both <c>Content-Length</c> and <c>Transfer-Encoding: chunked</c> are present.
    /// The two disagree on where the body ends — a request-smuggling vector that a
    /// proxy must refuse (RFC 9112 §6.3.3).
    /// </summary>
    Conflicting,
}

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

    /// <summary>
    /// Classifies how the request body is framed (see <see cref="RequestBodyFraming"/>).
    /// A message that declares both <c>Content-Length</c> and chunked
    /// <c>Transfer-Encoding</c> is <see cref="RequestBodyFraming.Conflicting"/> — the
    /// caller must refuse it rather than guess a body boundary.
    /// </summary>
    public static RequestBodyFraming DetectBodyFraming(IReadOnlyList<(string Name, string Value)> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var hasContentLength = false;
        var hasChunked = false;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
            }
            else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                hasChunked = true;
            }
        }

        return (hasChunked, hasContentLength) switch
        {
            (true, true) => RequestBodyFraming.Conflicting,
            (true, false) => RequestBodyFraming.Chunked,
            (false, true) => RequestBodyFraming.ContentLength,
            _ => RequestBodyFraming.None,
        };
    }

    /// <summary>
    /// Whether the request asks the proxy to acknowledge with <c>100 Continue</c>
    /// before sending its body (<c>Expect: 100-continue</c>, RFC 9110 §10.1.1).
    /// </summary>
    public static bool HasExpectContinue(IReadOnlyList<(string Name, string Value)> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase)
                && value.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
