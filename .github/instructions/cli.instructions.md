---
applyTo: "DevProxy/Commands/**,DevProxy/Program.cs,DevProxy/ApiControllers/**,DevProxy/ApiSecurity.cs,DevProxy/State/**,DevProxy/Stdio/**,DevProxy/Logging/**"
excludeAgent: "cloud-agent"
---

# Code review: CLI, local API and process state

Commands are built with System.CommandLine and registered in `DevProxyCommand.cs`. Arguments and options come from the local user and are trusted.

## Don't flag

- Path or injection concerns for option values (`--config-file`, config IDs, output paths). The user already has full filesystem access.
- Argument order or combinations that System.CommandLine's parser and validators already reject.
- Thread safety of static state in code that runs once per process (command handlers, startup).
- Requests to add more authentication to the local API. It's already protected by a per-process bearer token and an `Origin` allow-list (`ApiSecurity.cs`).

## Check

- **API protection stays intact.** New endpoints, middleware reordering or new hosting paths must still go through `ApiSecurity.CheckOriginAsync` and `ApiSecurity.AuthenticateAsync`. Token comparison must stay constant-time. Token files must keep using `PrivateFiles` (owner-only permissions), and the token must not be logged when `CI` is set.
- **Detached mode and state files.** `--detach`, `devproxy stop/status` and files under `DevProxy/State` track instances by PID. Watch for PID reuse (match the recorded `ProcessStartTime` before acting on a PID), stale state files after crashes, and races between instances.
- **Process handles.** `Process` objects must be disposed; child process paths and arguments with spaces must be quoted or passed via `ArgumentList`.
- **Exit codes and output.** Commands must return non-zero on failure and keep machine-readable output (`--output json`) free of extra log lines.
- **Option defaults and help.** Defaults in code, help text, config schema and docs must agree. Options that override config must not silently ignore the config value when unset.
- **Cross-platform.** Console behavior, signals (Ctrl+C), file paths and line endings on Windows, macOS and Linux.
- **Stdio proxying.** Messages from the wrapped process and its peer are untrusted traffic: framing, partial reads, large messages and encoding matter.
