using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace KestrelSpike;

/// <summary>
/// Spike connection handler. Exercises the risky cases from Phase 0B:
///  - selective decrypt: MITM watched hosts, blind-tunnel the rest byte-for-byte;
///  - non-destructive ClientHello ALPN peek → blind-tunnel h2-only (gRPC);
///  - keep-alive: multiple HTTP/1.1 requests per decrypted connection;
///  - SSE streaming pass-through (chunked, unbuffered).
/// </summary>
public sealed class ProxyConnectionHandler(CertificateAuthority ca, HttpClient httpClient, WatchedHosts watched)
    : ConnectionHandler
{
    private static readonly HashSet<string> HopByHopRequest = new(StringComparer.OrdinalIgnoreCase)
    { "Connection","Proxy-Connection","Keep-Alive","Transfer-Encoding","Upgrade","TE","Trailer","Proxy-Authorization","Host","Content-Length" };
    private static readonly HashSet<string> HopByHopResponse = new(StringComparer.OrdinalIgnoreCase)
    { "Connection","Proxy-Connection","Keep-Alive","Transfer-Encoding","Upgrade","Trailer","Content-Length","Content-Encoding" };

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        var reader = connection.Transport.Input;
        try
        {
            var firstLine = await ReadRequestLineAndHeadersAsync(reader, ct);
            if (firstLine is null)
            {
                return;
            }

            if (string.Equals(firstLine.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(connection, firstLine, ct);
            }
            else
            {
                // Plain HTTP proxy request (absolute-form). Forward + keep-alive loop.
                var clientStream = new DuplexPipeStream(connection.Transport);
                await ForwardAsync(clientStream, firstLine, firstLine.Target, ct);
                await PlainKeepAliveLoopAsync(clientStream, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[spike] error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- CONNECT: decide MITM vs blind tunnel via ALPN peek ------------------

    private async Task HandleConnectAsync(ConnectionContext connection, ParsedRequest connect, CancellationToken ct)
    {
        var (host, port) = SplitHostPort(connect.Target, 443);
        var reader = connection.Transport.Input;

        // Tell the client the tunnel is open; it will now start the TLS handshake.
        var clientStream = new DuplexPipeStream(connection.Transport);
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);

        // NON-DESTRUCTIVE peek of the ClientHello (SNI + ALPN).
        var hello = await PeekClientHelloAsync(reader, ct);

        var isWatched = watched.IsWatched(host);
        var h2Only = hello.Status == TlsClientHello.ParseStatus.Ok && hello.IsH2Only;

        Console.WriteLine($"[spike] CONNECT {host}:{port} watched={isWatched} alpn=[{string.Join(",", hello.Alpn)}] sni={hello.ServerName ?? "-"} hello={hello.Status}");

        if (!isWatched)
        {
            Console.WriteLine($"[spike] → blind-tunnel (not watched) {host}:{port}");
            await BlindTunnelAsync(clientStream, host, port, ct);
            return;
        }
        if (h2Only)
        {
            Console.WriteLine($"[spike] → blind-tunnel (h2-only/gRPC, never MITM) {host}:{port}");
            await BlindTunnelAsync(clientStream, host, port, ct);
            return;
        }

        // MITM: terminate TLS with our per-host cert, advertise http/1.1 only so any
        // h2-capable client downgrades and we intercept it as HTTP/1.1.
        Console.WriteLine($"[spike] → MITM (decrypt as http/1.1) {host}:{port}");
        var cert = ca.GetCertificateForHost(host);
        await using var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);

        await DecryptedKeepAliveLoopAsync(tls, host, port, ct);
    }

    // ---- keep-alive loops ----------------------------------------------------

    private async Task DecryptedKeepAliveLoopAsync(Stream tls, string host, int port, CancellationToken ct)
    {
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            var req = await ReadRequestFromStreamAsync(tls, ct);
            if (req is null)
            {
                break;
            }
            n++;
            var url = port == 443 ? $"https://{host}{req.Target}" : $"https://{host}:{port}{req.Target}";
            Console.WriteLine($"[spike]   keep-alive req #{n} on {host}: {req.Method} {req.Target}");
            var keepAlive = await ForwardAsync(tls, req, url, ct);
            if (!keepAlive)
            {
                break;
            }
        }
        Console.WriteLine($"[spike]   {host} decrypted connection closed after {n} request(s)");
    }

    private async Task PlainKeepAliveLoopAsync(Stream clientStream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var req = await ReadRequestFromStreamAsync(clientStream, ct);
            if (req is null)
            {
                break;
            }
            var keepAlive = await ForwardAsync(clientStream, req, req.Target, ct);
            if (!keepAlive)
            {
                break;
            }
        }
    }

    // ---- forwarding (with SSE/streaming) ------------------------------------

    private async Task<bool> ForwardAsync(Stream clientStream, ParsedRequest req, string url, CancellationToken ct)
    {
        using var outgoing = new HttpRequestMessage(new HttpMethod(req.Method), url);
        HttpContent? content = null;
        if (req.Body.Length > 0)
        {
            content = new ByteArrayContent(req.Body);
            outgoing.Content = content;
        }
        foreach (var (name, value) in req.Headers)
        {
            if (HopByHopRequest.Contains(name)) continue;
            if (!outgoing.Headers.TryAddWithoutValidation(name, value))
            {
                content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await httpClient.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
        foreach (var h in response.Headers)
        {
            if (HopByHopResponse.Contains(h.Key)) continue;
            foreach (var v in h.Value) sb.Append($"{h.Key}: {v}\r\n");
        }
        foreach (var h in response.Content.Headers)
        {
            if (HopByHopResponse.Contains(h.Key)) continue;
            foreach (var v in h.Value) sb.Append($"{h.Key}: {v}\r\n");
        }

        var contentLength = response.Content.Headers.ContentLength;
        var isEventStream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";

        // Stream (chunked) when length unknown or SSE; otherwise fixed length. Keep-alive preserved.
        if (contentLength is null || isEventStream)
        {
            sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: keep-alive\r\n\r\n");
            await WriteAsciiAsync(clientStream, sb.ToString(), ct);
            await StreamChunkedAsync(clientStream, response, isEventStream, ct);
        }
        else
        {
            sb.Append($"Content-Length: {contentLength}\r\n");
            sb.Append("Connection: keep-alive\r\n\r\n");
            await WriteAsciiAsync(clientStream, sb.ToString(), ct);
            await using var origin = await response.Content.ReadAsStreamAsync(ct);
            await origin.CopyToAsync(clientStream, ct);
            await clientStream.FlushAsync(ct);
        }
        return true; // keep-alive
    }

    private static async Task StreamChunkedAsync(Stream clientStream, HttpResponseMessage response, bool logChunks, CancellationToken ct)
    {
        await using var origin = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await origin.ReadAsync(buffer, ct)) > 0)
        {
            var sizeLine = Encoding.ASCII.GetBytes($"{read:X}\r\n");
            await clientStream.WriteAsync(sizeLine, ct);
            await clientStream.WriteAsync(buffer.AsMemory(0, read), ct);
            await clientStream.WriteAsync("\r\n"u8.ToArray(), ct);
            await clientStream.FlushAsync(ct); // flush each chunk => SSE arrives incrementally
            if (logChunks)
            {
                var preview = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 80)).Replace("\n", "\\n");
                Console.WriteLine($"[spike]     SSE chunk {read}B: {preview}");
            }
        }
        await clientStream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
        await clientStream.FlushAsync(ct);
    }

    // ---- blind tunnel --------------------------------------------------------

    private static async Task BlindTunnelAsync(Stream clientStream, string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        await using var origin = tcp.GetStream();
        var c2o = clientStream.CopyToAsync(origin, ct);
        var o2c = origin.CopyToAsync(clientStream, ct);
        await Task.WhenAny(c2o, o2c);
    }

    // ---- ClientHello peek (non-destructive) ---------------------------------

    private static async Task<TlsClientHello.Result> PeekClientHelloAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var parsed = TlsClientHello.Parse(buffer);

            if (parsed.Status != TlsClientHello.ParseStatus.NeedMore)
            {
                // CRITICAL: done peeking. AdvanceTo(start) sets consumed=examined=start,
                // i.e. nothing consumed AND nothing examined — so the next ReadAsync
                // (from SslStream / the tunnel) returns the SAME buffered ClientHello
                // immediately. Using examined=End here instead would deadlock: the pipe
                // would wait for MORE bytes than arrived before waking the next reader.
                reader.AdvanceTo(buffer.Start);
                return parsed;
            }

            // NeedMore: examine everything so the next ReadAsync waits for additional bytes.
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return new(TlsClientHello.ParseStatus.NeedMore, null, []);
            }
        }
    }

    // ---- request parsing -----------------------------------------------------

    private static async Task<ParsedRequest?> ReadRequestLineAndHeadersAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            if (TryParseHeaderBlock(buffer, out var headerEnd, out var parsed))
            {
                reader.AdvanceTo(headerEnd); // consume exactly the header block
                if (parsed!.ContentLength > 0)
                {
                    parsed.Body = await ReadBodyFromPipeAsync(reader, parsed.ContentLength, ct);
                }
                return parsed;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) return null;
        }
    }

    private static bool TryParseHeaderBlock(ReadOnlySequence<byte> buffer, out SequencePosition headerEnd, out ParsedRequest? parsed)
    {
        headerEnd = default; parsed = null;
        var arr = buffer.Length > 64 * 1024 ? buffer.Slice(0, 64 * 1024).ToArray() : buffer.ToArray();
        var idx = IndexOfDoubleCrlf(arr);
        if (idx < 0) return false;
        var headerText = Encoding.ASCII.GetString(arr, 0, idx);
        headerEnd = buffer.GetPosition(idx + 4);
        parsed = ParseHeaderText(headerText);
        return parsed is not null;
    }

    private static async Task<ParsedRequest?> ReadRequestFromStreamAsync(Stream stream, CancellationToken ct)
    {
        var acc = new List<byte>(2048);
        var buf = new byte[2048];
        int sep;
        while ((sep = IndexOfDoubleCrlf(acc)) < 0)
        {
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) return null;
            acc.AddRange(buf.AsSpan(0, read).ToArray());
            if (acc.Count > 256 * 1024) return null;
        }
        var headerText = Encoding.ASCII.GetString(acc.ToArray(), 0, sep);
        var parsed = ParseHeaderText(headerText);
        if (parsed is null) return null;

        var leftover = acc.GetRange(sep + 4, acc.Count - (sep + 4)).ToArray();
        if (parsed.ContentLength > 0)
        {
            var body = new byte[parsed.ContentLength];
            var copied = Math.Min(leftover.Length, parsed.ContentLength);
            Array.Copy(leftover, body, copied);
            var off = copied;
            while (off < parsed.ContentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(off), ct);
                if (read == 0) break;
                off += read;
            }
            parsed.Body = body;
        }
        return parsed;
    }

    private static ParsedRequest? ParseHeaderText(string headerText)
    {
        var lines = headerText.Split("\r\n");
        var start = lines[0].Split(' ', 3);
        if (start.Length < 3) return null;
        var headers = new List<(string, string)>();
        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            var name = lines[i][..c].Trim();
            var value = lines[i][(c + 1)..].Trim();
            headers.Add((name, value));
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var cl))
            {
                contentLength = cl;
            }
        }
        return new ParsedRequest(start[0], start[1], start[2], headers, contentLength);
    }

    private static async Task<byte[]> ReadBodyFromPipeAsync(PipeReader reader, int length, CancellationToken ct)
    {
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var toCopy = (int)Math.Min(buffer.Length, length - offset);
            buffer.Slice(0, toCopy).CopyTo(body.AsSpan(offset));
            offset += toCopy;
            reader.AdvanceTo(buffer.GetPosition(toCopy));
            if (result.IsCompleted && offset < length) break;
        }
        return body;
    }

    // ---- helpers -------------------------------------------------------------

    private static int IndexOfDoubleCrlf(IReadOnlyList<byte> d)
    {
        for (var i = 0; i + 3 < d.Count; i++)
        {
            if (d[i] == 13 && d[i + 1] == 10 && d[i + 2] == 13 && d[i + 3] == 10) return i;
        }
        return -1;
    }

    private static (string Host, int Port) SplitHostPort(string authority, int defaultPort)
    {
        var i = authority.LastIndexOf(':');
        if (i > 0 && int.TryParse(authority[(i + 1)..], out var p)) return (authority[..i], p);
        return (authority, defaultPort);
    }

    private static Task WriteAsciiAsync(Stream s, string text, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    internal sealed record ParsedRequest(string Method, string Target, string Version, List<(string Name, string Value)> Headers, int ContentLength)
    {
        public byte[] Body { get; set; } = [];
    }
}

public sealed class WatchedHosts(IEnumerable<string> patterns)
{
    private readonly string[] _patterns = patterns.ToArray();
    public bool IsWatched(string host) =>
        _patterns.Any(p => host.Equals(p, StringComparison.OrdinalIgnoreCase)
                        || (p.StartsWith("*.") && host.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)));
}
