// Node.js smoke client: built-in fetch and WebSocket (undici), no dependencies.
// Proxy and trust come from the environment, like a real app:
//   NODE_USE_ENV_PROXY=1, HTTPS_PROXY, NODE_EXTRA_CA_CERTS
// Env: SMOKE_TARGET_URL, SMOKE_SSE_MIN_SPAN_MS
// Prints one "RESULT<TAB>scenario<TAB>PASS|FAIL|SKIP<TAB>detail" line per scenario.

import { createHash, randomBytes } from 'node:crypto';

const target = process.env.SMOKE_TARGET_URL;
const sseMinSpanMs = Number(process.env.SMOKE_SSE_MIN_SPAN_MS);

const result = (scenario, status, detail = '') =>
  console.log(['RESULT', scenario, status, String(detail).replace(/\s+/g, ' ')].join('\t'));

async function scenario(name, fn) {
  try {
    const failure = await fn();
    result(name, failure ? 'FAIL' : 'PASS', failure ?? '');
  } catch (err) {
    result(name, 'FAIL', `${err.message}${err.cause ? `: ${err.cause.message ?? err.cause}` : ''}`);
  }
}

const timeout = () => AbortSignal.timeout(30_000);

await scenario('mock', async () => {
  const res = await fetch(`${target}/mocked`, { signal: timeout() });
  const body = await res.json();
  if (res.status !== 200 || res.headers.get('x-smoke-mock') !== 'yes' || body.mocked !== true) {
    return `status=${res.status} x-smoke-mock=${res.headers.get('x-smoke-mock')} body=${JSON.stringify(body)}`;
  }
});

await scenario('passthrough', async () => {
  const res = await fetch(`${target}/passthrough`, { headers: { 'x-smoke-request': 'hello-node' }, signal: timeout() });
  const body = await res.json();
  if (res.status !== 203 || res.headers.get('x-smoke-echo') !== 'hello-node' || body.smokeRequest !== 'hello-node') {
    return `status=${res.status} x-smoke-echo=${res.headers.get('x-smoke-echo')} body=${JSON.stringify(body)}`;
  }
});

await scenario('upload', async () => {
  // A ReadableStream body has no known length, so fetch sends it chunked.
  const chunks = Array.from({ length: 80 }, () => randomBytes(64 * 1024));
  const expected = createHash('sha256');
  chunks.forEach(c => expected.update(c));
  const expectedSha = expected.digest('hex');
  const expectedBytes = chunks.reduce((n, c) => n + c.length, 0);
  const stream = new ReadableStream({
    pull(controller) {
      const next = chunks.shift();
      next ? controller.enqueue(next) : controller.close();
    },
  });
  const res = await fetch(`${target}/upload`, {
    method: 'POST',
    body: stream,
    duplex: 'half',
    headers: { 'content-type': 'application/octet-stream' },
    signal: timeout(),
  });
  const body = await res.json();
  if (res.status !== 200 || body.bytes !== expectedBytes || body.sha256 !== expectedSha) {
    return `status=${res.status} body=${JSON.stringify(body)} expected bytes=${expectedBytes} sha256=${expectedSha}`;
  }
});

await scenario('sse', async () => {
  const res = await fetch(`${target}/sse`, { signal: timeout() });
  const events = [];
  const decoder = new TextDecoder();
  let pending = '';
  for await (const chunk of res.body) {
    pending += decoder.decode(chunk, { stream: true });
    let idx;
    while ((idx = pending.indexOf('\n')) >= 0) {
      const line = pending.slice(0, idx).replace(/\r$/, '');
      pending = pending.slice(idx + 1);
      if (line.startsWith('data:')) events.push({ data: line.slice(5).trim(), at: performance.now() });
    }
  }
  const ids = events.map(e => e.data).join(',');
  const span = events.length ? Math.round(events.at(-1).at - events[0].at) : 0;
  if (res.status !== 200 || ids !== '1,2,3' || span < sseMinSpanMs) {
    return `status=${res.status} events=[${ids}] span=${span}ms (min ${sseMinSpanMs}ms)`;
  }
});

await scenario('websocket', () => new Promise(resolve => {
  const ws = new WebSocket(`${target.replace(/^https/, 'wss')}/ws`);
  const binary = Uint8Array.from({ length: 256 }, (_, i) => i);
  const messages = ['hello-node', 'x'.repeat(70_000), binary];
  const received = [];
  const same = (a, b) => typeof b === 'string'
    ? a === b
    : a instanceof ArrayBuffer && Buffer.from(a).equals(Buffer.from(b));
  const describe = m => typeof m === 'string' ? `text:${m.length}` : `${m?.constructor?.name}:${m?.byteLength}`;
  let error = '';
  let settled = false;
  const done = failure => { if (!settled) { settled = true; clearTimeout(timer); resolve(failure); } };
  const timer = setTimeout(() => { done('timed out'); ws.close(); }, 30_000);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => messages.forEach(m => ws.send(m));
  ws.onmessage = e => {
    received.push(e.data);
    if (received.length === messages.length) ws.close(1000);
  };
  // Only a completed close handshake counts: the relay must deliver the server's Close reply.
  ws.onclose = e => {
    if (!e.wasClean || e.code !== 1000) return done(`unclean close: wasClean=${e.wasClean} code=${e.code}${error}`);
    if (received.length !== messages.length || !received.every((m, i) => same(m, messages[i]))) {
      return done(`echo mismatch: ${received.map(describe).join(',')}`);
    }
    done();
  };
  ws.onerror = e => { error = ` error=${e.message || e.error?.message || 'unknown'}`; };
}));
