# Windows verification checklist — Kestrel engine

Dev Proxy's HTTP(S) engine was migrated from Titanium/Unobtanium.Web.Proxy to a
Kestrel-based engine. Every Windows-specific code path is covered by unit tests, but the
**runtime behavior has never been live-verified on a real Windows host**. This checklist
walks through those paths end-to-end so we can sign off on a Windows release.

The three Windows-specific paths this checklist exists to prove are:

1. **System proxy on/off** via the WinINET registry + refresh (section 3).
2. **Root certificate trust** via the current-user Windows root store (section 2).
3. **Process filter** via `netstat` connection→PID resolution (section 5).

Everything else (build/tests, daemon lifecycle, interactive console, core proxy + plugin
smoke) is cross-platform but worth re-confirming on Windows.

Run it on a clean Windows 10/11 machine (or VM). Tick each box; record the actual result
in the **Notes** column. Anything that fails or surprises you is a finding worth filing.

- **Branch under test:** `waldekmastykarz-special-invention`
- **Tester:** _______________  **Date:** _______________  **Windows build:** _______________
- **.NET SDK:** `dotnet --version` → _______________ (expect .NET 10.x)

> Conventions used below:
> - `devproxy` means the built CLI. During verification you can run it from source with
>   `dotnet run --project DevProxy -- <args>` instead of installing.
> - PowerShell is assumed. Run an **elevated** prompt only where a step says so.

---

## 0. Prerequisites

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 0.1 | Install .NET 10 SDK; `dotnet --version` | Prints 10.x | ☐ | |
| 0.2 | `git clone` the repo, `git checkout waldekmastykarz-special-invention` | Branch checked out | ☐ | |
| 0.3 | Close other proxies/VPNs that set a system proxy | None active | ☐ | |
| 0.4 | Note current proxy state: `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyEnable` | Records baseline (usually `0x0`) | ☐ | |

---

## 1. Build & automated tests

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 1.1 | `dotnet build` at repo root | 0 errors (pre-existing CA2201 warning in `DevProxy.Proxy.Kestrel.Tests` is known/ignorable) | ☐ | |
| 1.2 | `dotnet test` | All projects green (~293 tests). Confirms `NetstatParser`, `RootTrustPolicy`, `SystemProxyAddress` unit tests pass **on Windows** | ☐ | |

If 1.2 fails, capture the failing test names before continuing — a parser that fails to
build on Windows invalidates later manual steps.

---

## 2. Root certificate trust (Windows root store)

Code: `DevProxy/Proxy/RootCertificateTrust.cs` → installs the **public** cert into
`X509Store(StoreName.Root, StoreLocation.CurrentUser)`. `cert remove`/`Untrust` removes it.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 2.1 | Open `certmgr.msc` → **Trusted Root Certification Authorities → Certificates**. Search for a Dev Proxy cert | None present (clean machine) | ☐ | |
| 2.2 | Run `devproxy` once (any config) and accept the trust prompt | CLI reports the cert was trusted | ☐ | |
| 2.3 | Refresh `certmgr.msc` | A Dev Proxy root cert now appears under current-user Trusted Root | ☐ | |
| 2.4 | With proxy running + watching a host, `curl https://<watched-host>/` **without** `-k` (or use Edge/Chrome, which use the Windows store) | Succeeds, no cert warning → MITM is trusted | ☐ | |
| 2.5 | Stop proxy. `devproxy cert remove` | CLI reports removal | ☐ | |
| 2.6 | Refresh `certmgr.msc` | Dev Proxy root cert is gone | ☐ | |
| 2.7 | Re-run `devproxy`; confirm a **new** root is minted + trusted and HTTPS still works | Regenerate-on-trust works | ☐ | |

---

## 3. System proxy on/off (WinINET registry)

Code: `DevProxy/Proxy/SystemProxyManager.cs` → sets HKCU `…\Internet Settings` `ProxyServer`
= `host:port` and `ProxyEnable` = `1` on start, `ProxyEnable` = `0` on stop, then calls
`InternetSetOption` so WinINET apps re-read without restart.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 3.1 | Start with system proxy ON, random port: `dotnet run --project DevProxy -- --as-system-proxy true --port 0` | Engine logs the bound port | ☐ | |
| 3.2 | `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyServer` | Shows `127.0.0.1:<boundPort>` | ☐ | |
| 3.3 | Same query for `/v ProxyEnable` | `0x1` | ☐ | |
| 3.4 | **Settings → Network & Internet → Proxy** | "Use a proxy server" ON, pointing at `127.0.0.1:<boundPort>` | ☐ | |
| 3.5 | In a **new** browser/app session, browse a watched URL | Request is intercepted + logged (proves WinINET refresh took effect without restart) | ☐ | |
| 3.6 | Stop the proxy (Ctrl+C, or `devproxy stop` from another terminal) | Engine exits | ☐ | |
| 3.7 | Re-query `ProxyEnable` | Back to `0x0` | ☐ | |
| 3.8 | Settings → Proxy UI | Proxy toggled OFF | ☐ | |

---

## 4. Detached daemon lifecycle (`--detach`, `status`, `stop`)

Regression-sensitive: the cut-over once orphaned the daemon on Windows-like flows. Confirm
the host writes daemon state and `status`/`stop` find it.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 4.1 | `dotnet run --project DevProxy -- --detach --as-system-proxy false --port 0 --api-port 0` | Parent returns; daemon keeps running | ☐ | |
| 4.2 | `devproxy status` | Shows running state with PID + bound port (fully populated) | ☐ | |
| 4.3 | `devproxy stop` | Daemon stops cleanly; status now shows stopped | ☐ | |
| 4.4 | Start detached again **with** `--as-system-proxy true --port 0`; then `devproxy stop` | System proxy is turned back OFF on stop (re-check reg `ProxyEnable` = `0x0`) | ☐ | |
| 4.5 | Start detached with system proxy ON, then kill the process hard (Task Manager → End task) and run `devproxy stop --force` | State self-heals; no orphaned proxy left in the registry/Settings | ☐ | |

---

## 5. Process filter (`netstat`-based)

Code: `DevProxy.Proxy.Kestrel/Internal/ProcessFilter.cs` →
`ConnectionProcessResolver.ResolveProcessId` runs `netstat -ano -p tcp` and feeds
`NetstatParser.ParsePid` to map a client source port → owning PID → process name.
Options: `--watch-pids`, `--watch-process-names`.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 5.1 | Find a target process PID (e.g. a specific browser): Task Manager → Details | Note the PID | ☐ | |
| 5.2 | Start: `dotnet run --project DevProxy -- --as-system-proxy false --port 0 --watch-pids <PID>` | Engine starts | ☐ | |
| 5.3 | Configure that process (or use its proxy settings) to send traffic through `127.0.0.1:<boundPort>` and browse a watched URL | Requests from the watched PID are intercepted + logged | ☐ | |
| 5.4 | Send traffic from a **different** process through the same proxy port | Those requests are **not** acted on (filtered out) | ☐ | |
| 5.5 | Restart with `--watch-process-names <name>` (e.g. `msedge`) instead of PID | Only that process's traffic is watched | ☐ | |

> If 5.3/5.4 misbehave, capture raw `netstat -ano -p tcp` output for the relevant port —
> it's the input to `NetstatParser` and pinpoints parser vs. resolution issues.

---

## 6. Interactive console (restored regression)

Code: `DevProxy/Proxy/InteractiveConsoleService.cs` + `ConsoleHotkeyHandler.cs`. Requires a
**real terminal** (not a redirected/piped stdin). Run directly, not detached.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 6.1 | `dotnet run --project DevProxy -- --as-system-proxy false --port 0` in an interactive terminal | After the "listening" log, the **hotkeys banner** prints | ☐ | |
| 6.2 | Press `r` | Recording starts (`◉ Recording...` indicator) | ☐ | |
| 6.3 | Make a request through the proxy, then press `s` | Recording stops; recorded requests are processed/output | ☐ | |
| 6.4 | Press `c` | Console clears and the banner reprints | ☐ | |
| 6.5 | Press `w` | Mock-request flow triggers | ☐ | |
| 6.6 | Ctrl+C | Proxy shuts down cleanly | ☐ | |
| 6.7 | Restart with `--record` | Recording is **already on** at launch (no keypress needed) | ☐ | |
| 6.8 | Restart with `--output json` (redirected/JSON mode) | API-instructions banner prints instead of hotkeys; key loop inactive | ☐ | |

---

## 7. Core proxy + plugin smoke

Confirm the engine itself behaves on Windows across protocols.

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 7.1 | Plain HTTP through the proxy to an `http://` site | 200, logged | ☐ | |
| 7.2 | HTTPS MITM to a **watched** host (no `-k`) | Decrypted + logged; cert trusted (from §2) | ☐ | |
| 7.3 | HTTPS to a **non-watched** host | Passes through (blind tunnel), still works, not decrypted | ☐ | |
| 7.4 | An HTTP/2 / gRPC endpoint | Works (downgrade+MITM if watched, else tunneled) | ☐ | |
| 7.5 | A WebSocket (`wss://`) endpoint | Connects and relays frames | ☐ | |
| 7.6 | Run with a config using `MockResponsePlugin` + a mocks file | Mocked response returned; origin not contacted | ☐ | |
| 7.7 | Run with `GenericRandomErrorPlugin` (or RateLimiting) | Simulated failures/limits observed | ☐ | |

---

## 8. Cleanup

| # | Step | Expected | Pass | Notes |
|---|------|----------|------|-------|
| 8.1 | `devproxy stop` (if anything still running) | Stopped | ☐ | |
| 8.2 | Confirm system proxy OFF (reg `ProxyEnable` = `0x0`, Settings UI) | OFF | ☐ | |
| 8.3 | `devproxy cert remove` (if you don't want the dev root left trusted) | Removed from `certmgr.msc` | ☐ | |
| 8.4 | Delete generated `devproxy-*.log` files | Removed | ☐ | |

---

## Sign-off

- [ ] All sections passed → **Windows runtime parity confirmed** (system proxy, root-store
      trust, and process filter all work live on Windows).
- [ ] Failures found (list below) → file findings against the migration branch.

**Findings / notes:**

```
(record any failures, surprises, raw netstat/registry output, or environment quirks here)
```
