// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// A single HTTP/1.x message head parsed off the wire: request line plus headers,
/// and any bytes already read past the header terminator.
/// </summary>
/// <param name="Method">Request method token (e.g. <c>GET</c>, <c>CONNECT</c>).</param>
/// <param name="Target">Request target (origin-form path, absolute-form URL, or CONNECT authority).</param>
/// <param name="Version">HTTP version token (e.g. <c>HTTP/1.1</c>).</param>
/// <param name="Headers">Headers in wire order (names/values trimmed).</param>
/// <param name="Leftover">Bytes read past the header block (start of the body).</param>
internal sealed record ParsedRequestHead(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<(string Name, string Value)> Headers,
    byte[] Leftover);

/// <summary>
/// Minimal, allocation-conscious HTTP/1.x reader for the proxy's decrypted/plain
/// side. Slice-1 scope: request line + headers + <c>Content-Length</c> bodies.
///
/// <para>
/// <b>Deferred (tracked hardening):</b> chunked transfer-decoding + trailers,
/// <c>Expect: 100-continue</c>, request-smuggling guards (duplicate/ambiguous
/// framing), and keep-alive leftover reuse. Until then the connection handler
/// reads one request and closes (<c>Connection: close</c>).
/// </para>
/// </summary>
internal static class Http1RequestReader
{
    private const int MaxHeaderBlockBytes = 1024 * 1024;

    /// <summary>Reads one request head, or <c>null</c> on a clean EOF before any bytes.</summary>
    public static async Task<ParsedRequestHead?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var headerBlock = await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);
        if (headerBlock is null)
        {
            return null;
        }

        var (headerText, leftover) = headerBlock.Value;
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

        return new ParsedRequestHead(startLine[0], startLine[1], startLine[2], headers, leftover);
    }

    /// <summary>
    /// Reads a fixed-length body given the <c>Content-Length</c> and any bytes the
    /// header read already consumed past the terminator.
    /// </summary>
    public static async Task<byte[]> ReadBodyAsync(Stream stream, byte[] leftover, int contentLength, CancellationToken ct)
    {
        if (contentLength <= 0)
        {
            return [];
        }

        var body = new byte[contentLength];
        var copied = Math.Min(leftover.Length, contentLength);
        Array.Copy(leftover, body, copied);

        var offset = copied;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }

        return body;
    }

    /// <summary>Reads the <c>Content-Length</c> header value, defaulting to 0.</summary>
    public static int GetContentLength(IReadOnlyList<(string Name, string Value)> headers)
    {
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

    private static async Task<(string Header, byte[] Leftover)?> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var accumulator = new List<byte>(4096);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            accumulator.AddRange(buffer.AsSpan(0, read));

            var terminator = IndexOfDoubleCrlf(accumulator);
            if (terminator >= 0)
            {
                var header = Encoding.ASCII.GetString(accumulator.ToArray(), 0, terminator);
                var leftover = accumulator.GetRange(terminator + 4, accumulator.Count - (terminator + 4)).ToArray();
                return (header, leftover);
            }

            if (accumulator.Count > MaxHeaderBlockBytes)
            {
                throw new InvalidOperationException("Request header block too large.");
            }
        }
    }

    private static int IndexOfDoubleCrlf(List<byte> data)
    {
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
