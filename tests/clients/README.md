# Client smoke tests

Runs real HTTP clients through a running Dev Proxy against a local HTTPS server and reports a client × scenario table. The point is to catch proxy behavior that only shows up with real client stacks, for example HTTP/1.1 framing, streaming, CONNECT/TLS and WebSocket upgrades. Unit tests with a hand-rolled client can miss these.

## Run

Linux only, because Dev Proxy is isolated by pointing `HOME`/`XDG_CONFIG_HOME` at a temp dir. Requirements: .NET 10 SDK, Node.js 24+, Python 3.11+, curl and openssl.

```bash
dotnet build DevProxy/DevProxy.csproj -c Release
dotnet build DevProxy.Plugins/DevProxy.Plugins.csproj -c Release
python3 -m pip install -r tests/clients/python/requirements.txt

DEVPROXY=$PWD/DevProxy/bin/Release/net10.0/devproxy tests/clients/run.sh            # all clients
DEVPROXY=$PWD/DevProxy/bin/Release/net10.0/devproxy tests/clients/run.sh node dotnet  # a subset
```

`run.sh` exits non-zero if any scenario fails. In GitHub Actions it also writes the table to the job summary (see `.github/workflows/client-smoke-tests.yml`).

The suite doesn't touch your profile, the OS trust store or the system proxy, and needs no external network access:

- Dev Proxy runs detached on random ports, with `--as-system-proxy false` and `--install-cert false`, under an isolated `HOME`/`XDG_CONFIG_HOME`.
- Each client trusts the exported Dev Proxy root certificate explicitly.
- Dev Proxy trusts the target server's throwaway CA through `SSL_CERT_FILE`.

## Layout

| Path | Purpose |
|---|---|
| `run.sh` | Orchestrator. It creates certificates, starts the server and Dev Proxy, runs the clients, and prints the table. |
| `server/server.mjs` | Zero-dependency HTTPS target server. |
| `devproxy/` | Dev Proxy config and mocks. The target port is injected with `${SMOKE_TARGET_PORT}`. |
| `curl/`, `node/`, `python/`, `dotnet/` | One script per client. |

## Scenarios

| Scenario | What it checks |
|---|---|
| `mock` | MockResponsePlugin answers `/mocked` (status, header, body). |
| `passthrough` | An unmocked request reaches the server and the server's status, header and body come back unchanged. |
| `upload` | A streamed 5 MiB request body arrives intact (byte count and SHA-256). |
| `sse` | Server-sent events arrive incrementally rather than buffered: 3 events sent 1 s apart must span at least 1.5 s at the client. |
| `websocket` | Text, binary and large messages echo, followed by a clean close handshake. |

## Client contract

Each client reads these environment variables:

- `SMOKE_PROXY_URL`
- `SMOKE_TARGET_URL`
- `SMOKE_CA_FILE`: Dev Proxy root certificate in PEM format.
- `SMOKE_SSE_MIN_SPAN_MS`

For each scenario, a client prints one line to stdout:

```text
RESULT<TAB><scenario><TAB><PASS|FAIL|SKIP><TAB><detail>
```

A scenario with no `RESULT` line counts as a failure. To add a client, add a folder with a script that follows this contract and add a case for it in `run_client` in `run.sh`.
