// Local HTTPS target server for the client smoke tests. Zero dependencies so it
// runs anywhere Node runs, with no network access.
//
//   GET  /passthrough  203 + echoes the X-Smoke-Request header and request info
//   POST /upload       reads the whole body, returns its length and SHA-256
//   GET  /sse          three server-sent events, one second apart
//   GET  /ws           WebSocket echo
//   anything else      404 (so a missing mock is visible)
//
// Usage: node server.mjs <cert.pem> <key.pem> <port-file>

import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:https';

const SSE_EVENTS = 3;
const SSE_GAP_MS = 1000;
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

const [certFile, keyFile, portFile] = process.argv.slice(2);
if (!certFile || !keyFile || !portFile) {
  console.error('Usage: node server.mjs <cert.pem> <key.pem> <port-file>');
  process.exit(2);
}

const server = createServer({ cert: readFileSync(certFile), key: readFileSync(keyFile) }, (req, res) => {
  const url = new URL(req.url, 'https://localhost');
  const framing = req.headers['transfer-encoding'] ?? `content-length=${req.headers['content-length'] ?? 0}`;
  console.log(`${req.method} ${url.pathname} ${framing}`);

  if (req.method === 'GET' && url.pathname === '/passthrough') {
    const smokeRequest = req.headers['x-smoke-request'] ?? '';
    res.writeHead(203, {
      'content-type': 'application/json',
      'x-smoke-echo': smokeRequest,
    });
    res.end(JSON.stringify({ path: url.pathname, smokeRequest }));
    return;
  }

  if (req.method === 'POST' && url.pathname === '/upload') {
    const hash = createHash('sha256');
    let bytes = 0;
    req.on('data', chunk => {
      bytes += chunk.length;
      hash.update(chunk);
    });
    req.on('end', () => {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ bytes, sha256: hash.digest('hex') }));
    });
    return;
  }

  if (req.method === 'GET' && url.pathname === '/sse') {
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
    res.flushHeaders();
    let n = 0;
    const send = () => {
      n++;
      res.write(`id: ${n}\nevent: smoke\ndata: ${n}\n\n`);
      if (n < SSE_EVENTS) {
        setTimeout(send, SSE_GAP_MS);
      } else {
        res.end();
      }
    };
    send();
    return;
  }

  res.writeHead(404, { 'content-type': 'text/plain' });
  res.end('not found (expected a mock response from Dev Proxy?)');
});

server.on('upgrade', (req, socket) => {
  const key = req.headers['sec-websocket-key'];
  if (new URL(req.url, 'https://localhost').pathname !== '/ws' || !key) {
    socket.end('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n');
    return;
  }
  console.log(`UPGRADE ${req.url}`);
  const accept = createHash('sha1').update(key + WS_GUID).digest('base64');
  socket.setNoDelay(true);
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  echoWebSocket(socket);
});

// Minimal RFC 6455 echo: enough for unfragmented text/binary frames, ping and close.
function echoWebSocket(socket) {
  let buffer = Buffer.alloc(0);
  socket.on('error', () => socket.destroy());
  socket.on('data', data => {
    buffer = Buffer.concat([buffer, data]);
    for (;;) {
      if (buffer.length < 2) return;
      const opcode = buffer[0] & 0x0f;
      const masked = (buffer[1] & 0x80) !== 0;
      let length = buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (buffer.length < 4) return;
        length = buffer.readUInt16BE(2);
        offset = 4;
      } else if (length === 127) {
        if (buffer.length < 10) return;
        length = Number(buffer.readBigUInt64BE(2));
        offset = 10;
      }
      const maskOffset = offset;
      if (masked) offset += 4;
      if (buffer.length < offset + length) return;

      const payload = Buffer.from(buffer.subarray(offset, offset + length));
      if (masked) {
        for (let i = 0; i < payload.length; i++) payload[i] ^= buffer[maskOffset + (i % 4)];
      }
      buffer = buffer.subarray(offset + length);

      if (opcode === 0x8) {
        socket.end(frame(0x8, payload.subarray(0, 2)));
        return;
      }
      if (opcode === 0x9) {
        socket.write(frame(0xa, payload));
      } else if (opcode === 0x1 || opcode === 0x2) {
        socket.write(frame(opcode, payload));
      }
    }
  });
}

function frame(opcode, payload) {
  let header;
  if (payload.length < 126) {
    header = Buffer.from([0x80 | opcode, payload.length]);
  } else if (payload.length < 65536) {
    header = Buffer.alloc(4);
    header[0] = 0x80 | opcode;
    header[1] = 126;
    header.writeUInt16BE(payload.length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x80 | opcode;
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  return Buffer.concat([header, payload]);
}

server.listen(0, '127.0.0.1', () => {
  const { port } = server.address();
  writeFileSync(portFile, String(port));
  console.log(`listening on https://localhost:${port}`);
});
