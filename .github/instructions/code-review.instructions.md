---
applyTo: "**"
excludeAgent: "cloud-agent"
---

# Code review: threat model and priorities

Dev Proxy is a local developer tool. The developer who runs it already has full access to the machine, so prioritize correctness bugs over theoretical hardening. Leave fewer, higher-confidence comments; skip a comment if you can't describe a concrete failure.

## Trust boundary

- **Untrusted:** intercepted network traffic (requests, responses, headers, bodies, WebSocket frames, TLS handshakes), remote content Dev Proxy downloads (OpenAPI specs, API Center, update checks, LLM responses) and messages from other local processes calling the API.
- **Trusted:** CLI arguments, config files (`devproxyrc.json`, mocks, errors, rewrites files), env vars and paths the user supplies. The user can already read and write any file they point Dev Proxy at.

## Don't flag

- Path traversal, injection or "validate this path" on user-supplied CLI args, config values or file paths.
- Missing auth on the local API in general. The API binds to `127.0.0.1` by default and is protected by a per-process bearer token plus an `Origin` allow-list (`DevProxy/ApiSecurity.cs`, wired in `Program.cs`). Do flag a change that bypasses or weakens that middleware.
- Thread safety or static state on single-instance CLI code paths (commands that run once and exit).
- Wrapping `Dispose`/cleanup in extra try/catch, or disposal concerns when ownership is already handled by `using`, DI or the host.
- Argument combinations System.CommandLine already rejects or can't produce.
- Template or placeholder files (for example `DevProxy/config-templates/`) that aren't valid on their own.
- Style or naming nits already covered by `.editorconfig` and the analyzers in `Directory.Build.props`.
- Behavior the PR description or code comments state is intentional.

## Do scrutinize

- **Unbounded resource use from untrusted input:** buffering whole bodies, collections or strings sized by traffic without a cap.
- **Culture and time:** parsing or formatting numbers/dates without `CultureInfo.InvariantCulture`, local time where UTC is required, `DateTime.Now` in persisted or compared timestamps, encoding assumptions (UTF-8 vs. charset header, BOMs).
- **Off-by-one and probability checks:** for example `Random.Next(0, 100) < rate` boundaries at 0 and 100.
- **Process handling:** undisposed `Process` objects, PID reuse when tracking or killing processes, script/executable paths with spaces not quoted, platform-specific behavior on Windows, macOS and Linux.
- **Schema vs. runtime defaults:** a plugin's C# default must match the `default` in its JSON schema and docs.
- **Hot reload:** state that must survive or must reset when the config file changes and the proxy restarts.
- **Error handling that hides failures:** swallowed exceptions on paths the user relies on for feedback.

## Severity

Start every comment with `**Blocking:**` or `**Advisory:**`.

- **Blocking:** a correctness bug, a security issue per the trust boundary above, data loss, a breaking change to a public API, CLI option or JSON schema, or new behavior with no test that would catch it breaking. Must be fixed before merge.
- **Advisory:** everything else. The author fixes it or replies; it doesn't need another review round.

If you're unsure, use Advisory. Don't label a style or naming nit as Blocking.

When in doubt, ask a question instead of asserting a bug.
