// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Proxy.Kestrel.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace DevProxy.Proxy.Kestrel.Internal;

/// <summary>
/// Handles one raw TCP connection: parses the proxy request, terminates TLS for
/// watched <c>CONNECT</c> targets, runs the plugin pipeline against the canonical
/// model, forwards to the origin, and writes the response back.
///
/// <para>
/// Slice-1 scope: one request per connection (<c>Connection: close</c>), plain
/// HTTP + HTTPS-via-CONNECT, mocking short-circuit. Deferred hardening (tracked):
/// keep-alive, blind-tunnel for non-watched / h2-only (ClientHello peek), WebSocket
/// relay + inspection, chunked/trailers, body-mode streaming.
/// </para>
/// </summary>
internal sealed class ProxyConnectionHandler(
    CertificateAuthority ca,
    UpstreamForwarder forwarder,
    PluginPipeline pipeline,
    ILogger logger) : ConnectionHandler
{
    private static int _requestCounter;

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var clientStream = new DuplexPipeStream(connection.Transport);

        try
        {
            var head = await Http1RequestReader.ReadHeadAsync(clientStream, ct).ConfigureAwait(false);
            if (head is null)
            {
                return;
            }

            if (string.Equals(head.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectAsync(clientStream, head, ct).ConfigureAwait(false);
            }
            else
            {
                // Plain HTTP proxy request: the target is an absolute URL.
                await ExchangeAsync(clientStream, head, head.Target, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ConnectionResetException)
        {
            // Client disconnect / cancellation — normal teardown, not an error.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling proxy connection");
        }
    }

    private async Task HandleConnectAsync(Stream clientStream, ParsedRequestHead connect, CancellationToken ct)
    {
        var (host, portPart) = SplitHostPort(connect.Target);
        var port = portPart ?? 443;

        // Slice-1: always MITM. Deferred: blind-tunnel non-watched hosts and
        // h2-only (gRPC) clients via a ClientHello ALPN peek.
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

        var certificate = ca.GetCertificateForHost(host);
        await using var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct).ConfigureAwait(false);

        var head = await Http1RequestReader.ReadHeadAsync(tls, ct).ConfigureAwait(false);
        if (head is null)
        {
            return;
        }

        var url = port == 443
            ? $"https://{host}{head.Target}"
            : $"https://{host}:{port.ToString(CultureInfo.InvariantCulture)}{head.Target}";

        await ExchangeAsync(tls, head, url, ct).ConfigureAwait(false);
    }

    private async Task ExchangeAsync(Stream clientStream, ParsedRequestHead head, string absoluteUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var requestUri))
        {
            await WriteErrorAsync(clientStream, HttpStatusCode.BadRequest, "Malformed request target", ct).ConfigureAwait(false);
            return;
        }

        var contentLength = Http1RequestReader.GetContentLength(head.Headers);
        var body = await Http1RequestReader.ReadBodyAsync(clientStream, head.Leftover, contentLength, ct).ConfigureAwait(false);

        var headers = new HeaderCollection();
        foreach (var (name, value) in head.Headers)
        {
            headers.Add(name, value);
        }

        var version = ParseHttpVersion(head.Version);
        var request = new MutableHttpRequest(head.Method, requestUri, version, headers, body);
        var session = new CanonicalProxySession(
            Guid.NewGuid().ToString("n"),
            request,
            processId: null,
            requestId: Interlocked.Increment(ref _requestCounter));

        RequestPhase phase;
        try
        {
            phase = await pipeline.RunRequestAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pipeline.Forget(session.SessionId);
            logger.LogError(ex, "Error running request pipeline");
            await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Plugin pipeline error", ct).ConfigureAwait(false);
            return;
        }

        // Mocked: a plugin produced the response during the request phase. Skip the
        // upstream forward, but still run the response pipeline so reporters/loggers
        // observe the mock and the console formatter flushes its buffered request log.
        if (phase == RequestPhase.Mocked)
        {
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, ct).ConfigureAwait(false);
            return;
        }

        MutableHttpResponse response;
        try
        {
            response = await forwarder.ForwardAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pipeline.Forget(session.SessionId);
            logger.LogError(ex, "Error forwarding to origin {Url}", absoluteUrl);
            await WriteErrorAsync(clientStream, HttpStatusCode.BadGateway, "Upstream request failed", ct).ConfigureAwait(false);
            return;
        }

        if (phase == RequestPhase.Watched)
        {
            session.SetOriginResponse(response);
            await pipeline.RunResponseAsync(session, ct).ConfigureAwait(false);
            await ResponseWriter.WriteAsync(clientStream, session.MutableResponse!, ct).ConfigureAwait(false);
        }
        else
        {
            // NotWatched: pure passthrough, no response-phase plugins.
            await ResponseWriter.WriteAsync(clientStream, response, ct).ConfigureAwait(false);
        }
    }

    private static Version ParseHttpVersion(string token)
    {
        // token like "HTTP/1.1"
        var slash = token.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0 && Version.TryParse(token[(slash + 1)..], out var version))
        {
            return version;
        }
        return HttpVersion.Version11;
    }

    private static async Task WriteErrorAsync(Stream clientStream, HttpStatusCode status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {(int)status} {ReasonPhrase(status)}\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        try
        {
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await clientStream.WriteAsync(body, ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Client already gone.
        }
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Bad Request",
        HttpStatusCode.BadGateway => "Bad Gateway",
        _ => status.ToString(),
    };

    private static (string Host, int? Port) SplitHostPort(string authority)
    {
        var separator = authority.LastIndexOf(':');
        if (separator > 0 && int.TryParse(authority[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (authority[..separator], port);
        }
        return (authority, null);
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();
}
