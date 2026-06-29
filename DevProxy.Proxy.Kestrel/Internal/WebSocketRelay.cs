// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Relays a WebSocket connection between the client and the origin. A WebSocket
/// handshake cannot go through <see cref="UpstreamForwarder"/> (pooled
/// <see cref="HttpClient"/>): it needs the raw, long-lived socket that survives the
/// <c>101 Switching Protocols</c> response. This mirrors what the Titanium engine
/// does today — frames are relayed verbatim and opaque; no plugin inspects them.
///
/// <code>
///   client                         proxy                          origin
///     │  GET … Upgrade: websocket    │                              │
///     │ ───────────────────────────► │  (replay handshake, origin-  │
///     │                              │   form, preserving Upgrade/   │
///     │                              │   Connection/Sec-WebSocket-*) ─►
///     │                              │  ◄─ 101 Switching Protocols ──│
///     │ ◄── 101 (verbatim) ──────────│                              │
///     │ ◄═══════════ raw WebSocket frames spliced both ways ═══════► │
/// </code>
///
/// <para>
/// The <c>101</c> is written to the client <b>verbatim</b> (never via
/// <see cref="ResponseWriter"/>, which strips <c>Upgrade</c>/<c>Connection</c> and
/// injects <c>Content-Length</c> — that would corrupt the handshake).
/// </para>
///
/// <para>
/// <b>Deferred (tracked, see plan §7):</b> decoding frames into messages and exposing
/// them to plugins for inspection/mocking. Until then this is a transparent relay.
/// </para>
/// </summary>
internal sealed class WebSocketRelay(ILogger logger)
{
    private const int MaxResponseHeadBytes = 64 * 1024;

    /// <summary>
    /// Connects to the origin, replays the upgrade handshake, invokes
    /// <paramref name="onHandshakeResponse"/> with the origin's parsed response (so the
    /// caller can run the response pipeline / log it), writes that response back to the
    /// client verbatim, and — on <c>101</c> — splices frames in both directions until
    /// either peer closes.
    /// </summary>
    public async Task RelayAsync(
        Stream clientStream,
        IHttpRequest request,
        Uri origin,
        Func<MutableHttpResponse, Task> onHandshakeResponse,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(onHandshakeResponse);

        var useTls = string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(origin.Host, origin.Port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "WebSocket connect to {Host}:{Port} failed", origin.Host, origin.Port);
            return;
        }

        // leaveInnerStreamOpen: false on the SslStream disposes the NetworkStream; the
        // NetworkStream is also owned by the `using` TcpClient. `await using` here
        // disposes whichever stream we end up relaying over.
        await using var originStream = await OpenOriginStreamAsync(tcp, origin, useTls, ct).ConfigureAwait(false);

        await WriteHandshakeAsync(originStream, request, origin, ct).ConfigureAwait(false);

        var head = await ReadResponseHeadAsync(originStream, ct).ConfigureAwait(false);
        if (head is null)
        {
            logger.LogDebug("WebSocket origin {Host} closed before sending a handshake response", origin.Host);
            return;
        }

        var (statusCode, reason, headers, rawHead, leftover) = head.Value;

        var response = new MutableHttpResponse(
            (HttpStatusCode)statusCode, HttpVersion.Version11, headers, ReadOnlyMemory<byte>.Empty, reason);
        await onHandshakeResponse(response).ConfigureAwait(false);

        // Write the origin's handshake response to the client verbatim.
        await clientStream.WriteAsync(rawHead, ct).ConfigureAwait(false);
        if (leftover.Length > 0)
        {
            await clientStream.WriteAsync(leftover, ct).ConfigureAwait(false);
        }
        await clientStream.FlushAsync(ct).ConfigureAwait(false);

        if (statusCode != (int)HttpStatusCode.SwitchingProtocols)
        {
            // Origin declined the upgrade. We've relayed its response; there's no
            // tunnel to splice. Close (a non-101 may carry a body we don't frame yet).
            logger.LogDebug("WebSocket origin {Host} declined upgrade with {Status}", origin.Host, statusCode);
            return;
        }

        logger.LogDebug("WebSocket {Scheme}://{Host}:{Port}{Path} established",
            useTls ? "wss" : "ws", origin.Host, origin.Port, origin.PathAndQuery);

        await StreamRelay.RelayBidirectionalAsync(clientStream, originStream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the origin stream, layering TLS (with http/1.1 ALPN, matching our downgrade
    /// policy) for <c>wss</c>. On a TLS failure the half-built <see cref="SslStream"/> is
    /// disposed before the exception propagates.
    /// </summary>
    private static async Task<Stream> OpenOriginStreamAsync(TcpClient tcp, Uri origin, bool useTls, CancellationToken ct)
    {
        var network = tcp.GetStream();
        if (!useTls)
        {
            return network;
        }

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = origin.Host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, ct).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ssl;
    }

    /// <summary>
    /// Replays the handshake to the origin in origin-form. WebSocket-essential headers
    /// (<c>Upgrade</c>, <c>Connection</c>, <c>Sec-WebSocket-*</c>) MUST be preserved —
    /// they are normally hop-by-hop but are exactly what the handshake needs — so only
    /// the proxy-scoped headers are dropped.
    /// </summary>
    private static async Task WriteHandshakeAsync(Stream origin, IHttpRequest request, Uri target, CancellationToken ct)
    {
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{request.Method} {target.PathAndQuery} HTTP/1.1\r\n");

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _ = builder.Append(CultureInfo.InvariantCulture, $"{header.Name}: {header.Value}\r\n");
        }
        _ = builder.Append("\r\n");

        await origin.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
        await origin.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the origin's HTTP response head (status line + headers) up to the
    /// terminating CRLFCRLF, returning the parsed status/headers, the exact raw head
    /// bytes (to relay verbatim) and any bytes read past the head (the first frame).
    /// Returns null on EOF before a complete head arrived.
    /// </summary>
    private static async Task<(int StatusCode, string Reason, HeaderCollection Headers, byte[] RawHead, byte[] Leftover)?>
        ReadResponseHeadAsync(Stream origin, CancellationToken ct)
    {
        var accumulator = new List<byte>(512);
        var buffer = new byte[4096];

        while (true)
        {
            var terminator = Http1RequestReader.IndexOfDoubleCrlf(accumulator);
            if (terminator >= 0)
            {
                var headEnd = terminator + 4;
                var rawHead = accumulator.GetRange(0, headEnd).ToArray();
                var leftover = accumulator.Count > headEnd
                    ? accumulator.GetRange(headEnd, accumulator.Count - headEnd).ToArray()
                    : [];

                var headerText = Encoding.ASCII.GetString(accumulator.ToArray(), 0, terminator);
                var (statusCode, reason, headers) = ParseResponseHead(headerText);
                return (statusCode, reason, headers, rawHead, leftover);
            }

            if (accumulator.Count > MaxResponseHeadBytes)
            {
                throw new InvalidOperationException("WebSocket origin response head too large.");
            }

            var read = await origin.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            accumulator.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Parses a response head block: <c>HTTP/1.1 101 Switching Protocols</c> followed
    /// by header lines.
    /// </summary>
    internal static (int StatusCode, string Reason, HeaderCollection Headers) ParseResponseHead(string headerText)
    {
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Empty WebSocket origin response head.");
        }

        var statusLine = lines[0];
        var firstSpace = statusLine.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            throw new InvalidOperationException($"Malformed status line: '{statusLine}'.");
        }

        var afterVersion = statusLine[(firstSpace + 1)..].TrimStart();
        var secondSpace = afterVersion.IndexOf(' ', StringComparison.Ordinal);
        var codeToken = secondSpace < 0 ? afterVersion : afterVersion[..secondSpace];
        if (!int.TryParse(codeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidOperationException($"Malformed status code in '{statusLine}'.");
        }
        var reason = secondSpace < 0 ? string.Empty : afterVersion[(secondSpace + 1)..];

        var headers = new HeaderCollection();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            headers.Add(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return (statusCode, reason, headers);
    }
}
