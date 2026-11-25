---
applyTo: "DevProxy.Proxy.Kestrel/**,DevProxy/Proxy/**"
excludeAgent: "cloud-agent"
---

# Code review: proxy engine

`DevProxy.Proxy.Kestrel` is a custom forward proxy and TLS MITM built on Kestrel. Everything it reads from clients and upstream servers is untrusted. HTTP correctness bugs here break every user, so this is where review time pays off most.

## HTTP/1.1 correctness (RFC 9110/9112)

- `Content-Length` vs. `Transfer-Encoding: chunked`: never send both; recompute or drop `Content-Length` after a plugin changes the body; handle requests with a body but no length; reject or normalize conflicting/duplicate `Content-Length` values.
- Chunked bodies: correct chunk parsing (extensions, trailers, terminating `0\r\n\r\n`), no assumption that a chunk arrives in one read.
- Hop-by-hop headers (`Connection`, `Keep-Alive`, `Proxy-Connection`, `Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`, and any header named in `Connection`) must not be forwarded as-is.
- Responses with no body (`HEAD`, `1xx`, `204`, `304`) and `Expect: 100-continue`.
- Keep-alive and connection reuse: leftover bytes from a previous message, pipelined requests, half-closed connections.
- `CONNECT` authority parsing (IPv6 literals, missing/invalid ports) and TLS ClientHello/SNI handling.

## WebSockets

- Upgrade handshake: `Sec-WebSocket-Key`/`Accept`, `Upgrade`/`Connection` tokens (case-insensitive, comma lists), subprotocol and extension negotiation.
- Relay must handle fragmented and control frames and close both sides cleanly.

## Resources and lifetime

- Unbounded buffering of bodies, headers or frames sized by traffic. Flag new code that reads a whole stream into memory without a limit or that grows a collection per request without eviction.
- Every `Stream`, `PipeReader`/`PipeWriter`, `SslStream`, `HttpClient` response and socket must be disposed or completed on all paths, including cancellation and upstream failure.
- Cancellation and timeouts: idle timers, upstream failures and client disconnects must not leave tasks running or connections open.

## Plugin pipeline

- Plugins run in config order; a plugin can mutate the request (for example `RewritePlugin` changes `RequestUri`). Check that URL matching, host watch lists and logging use the right (original vs. rewritten) URL.
- A plugin setting a mocked response must short-circuit the upstream call; later plugins must still see consistent state.
- Exceptions thrown by a plugin must not tear down the connection silently.

## Platform

- System proxy, certificate trust and environment helpers in `DevProxy/Proxy` run per-OS. Check the Windows, macOS and Linux branches separately and make sure system settings are restored on exit and crash.
