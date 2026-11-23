"""Python smoke client: httpx for HTTP and websockets for WebSocket.

Trust comes from SSL_CERT_FILE; the proxy is passed explicitly.
Env: SMOKE_PROXY_URL, SMOKE_TARGET_URL, SSL_CERT_FILE, SMOKE_SSE_MIN_SPAN_MS
Prints one "RESULT<TAB>scenario<TAB>PASS|FAIL|SKIP<TAB>detail" line per scenario.
"""

import hashlib
import os
import ssl
import time

import httpx
from websockets.sync.client import connect

PROXY = os.environ["SMOKE_PROXY_URL"]
TARGET = os.environ["SMOKE_TARGET_URL"]
SSE_MIN_SPAN_MS = int(os.environ["SMOKE_SSE_MIN_SPAN_MS"])
SSL_CONTEXT = ssl.create_default_context(cafile=os.environ["SSL_CERT_FILE"])


def result(scenario, status, detail=""):
    print("\t".join(["RESULT", scenario, status, " ".join(str(detail).split())]), flush=True)


def run(name, fn):
    try:
        failure = fn()
        result(name, "FAIL" if failure else "PASS", failure or "")
    except Exception as err:  # noqa: BLE001 - any error is a scenario failure
        result(name, "FAIL", f"{type(err).__name__}: {err}")


client = httpx.Client(proxy=PROXY, verify=SSL_CONTEXT, timeout=30, trust_env=False)


def mock():
    res = client.get(f"{TARGET}/mocked")
    if res.status_code != 200 or res.headers.get("x-smoke-mock") != "yes" or res.json().get("mocked") is not True:
        return f"status={res.status_code} x-smoke-mock={res.headers.get('x-smoke-mock')} body={res.text[:200]}"
    return None


def passthrough():
    res = client.get(f"{TARGET}/passthrough", headers={"x-smoke-request": "hello-python"})
    if (
        res.status_code != 203
        or res.headers.get("x-smoke-echo") != "hello-python"
        or res.json().get("smokeRequest") != "hello-python"
    ):
        return f"status={res.status_code} x-smoke-echo={res.headers.get('x-smoke-echo')} body={res.text[:200]}"
    return None


def upload():
    chunks = [os.urandom(64 * 1024) for _ in range(80)]
    expected_sha = hashlib.sha256(b"".join(chunks)).hexdigest()
    expected_bytes = sum(len(c) for c in chunks)
    # A generator body has no known length, so httpx sends it chunked.
    res = client.post(
        f"{TARGET}/upload",
        content=(c for c in chunks),
        headers={"content-type": "application/octet-stream"},
    )
    body = res.json()
    if res.status_code != 200 or body.get("bytes") != expected_bytes or body.get("sha256") != expected_sha:
        return f"status={res.status_code} body={body} expected bytes={expected_bytes} sha256={expected_sha}"
    return None


def sse():
    events = []
    with client.stream("GET", f"{TARGET}/sse") as res:
        for line in res.iter_lines():
            if line.startswith("data:"):
                events.append((line[5:].strip(), time.monotonic()))
    ids = ",".join(e[0] for e in events)
    span = round((events[-1][1] - events[0][1]) * 1000) if events else 0
    if res.status_code != 200 or ids != "1,2,3" or span < SSE_MIN_SPAN_MS:
        return f"status={res.status_code} events=[{ids}] span={span}ms (min {SSE_MIN_SPAN_MS}ms)"
    return None


def websocket():
    url = TARGET.replace("https://", "wss://", 1) + "/ws"
    messages = ["hello-python", "x" * 70_000]
    with connect(url, proxy=PROXY, ssl=SSL_CONTEXT, open_timeout=30, max_size=None) as ws:
        for m in messages:
            ws.send(m)
        received = [ws.recv(timeout=30) for _ in messages]
        ws.close()
        # Only a completed close handshake counts: the relay must deliver the server's Close reply.
        close_rcvd = ws.protocol.close_rcvd
    if received != messages:
        return f"echo mismatch: {[len(m) for m in received]}"
    if close_rcvd is None or close_rcvd.code != 1000:
        return f"unclean close: received={close_rcvd}"
    return None


run("mock", mock)
run("passthrough", passthrough)
run("upload", upload)
run("sse", sse)
run("websocket", websocket)
