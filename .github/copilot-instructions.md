# Copilot Instructions for Dev Proxy

## Overview

Dev Proxy is a cross-platform .NET API simulator and proxy for testing how applications handle API failures, rate limits, latency, and more.

## Critical (Non-Obvious)

- **Proxy engine:** Uses [svrooij/unobtanium-web-proxy](https://github.com/svrooij/unobtanium-web-proxy) (fork of Titanium.Web.Proxy)
- **Config tokens:** Paths in config files can use `~appFolder` for resolution
- **MCP Server:** Use [@devproxy/mcp](https://www.npmjs.com/package/@devproxy/mcp) to programmatically retrieve up-to-date docs and JSON schemas

## Best Practices

See the comprehensive best practices guide: https://raw.githubusercontent.com/dev-proxy-tools/mcp/refs/heads/main/assets/best-practices.md

Key points:
- Store Dev Proxy files in `.devproxy` folder in the workspace
- Schema version in `$schema` must match installed Dev Proxy version
- In config files: `plugins` first, then `urlsToWatch`, then plugin configs
- Plugin order matters: latency plugins first, response simulators before reporters, reporters last
- URL matching is order-dependent: most specific URLs first, use `!` prefix to exclude
- File paths are relative to the config file where they're defined
- Hot reload (v2.1.0+): config changes apply automatically without restart

## Plugin Development

To create a new plugin:
1. Pick the relevant interface: `IProxyPlugin` (HTTP interception) or `IStdioPlugin` (stdio communication)
2. Implement as a **public class** in a class library project
3. Override the relevant methods/events for your use case

## Commits & Releases

- **Commit messages:** Clear, succinct, and reference the issue they close when applicable
- **Versioning:** We follow semver. Use the `upgrade-devproxy-version` skill for version bumps
- **Release:** After release builds, homebrew and winget are updated manually

## Testing Code Changes

When testing code, features, or changes, **always launch the proxy in detached mode** with these settings to avoid conflicts:

```bash
dotnet run --project DevProxy -- --as-system-proxy false --port 0 --api-port 0
```

- `--as-system-proxy false` — prevents modifying system-wide network settings
- `--port 0` — lets the OS assign a random available port for the proxy
- `--api-port 0` — lets the OS assign a random available port for the API

**Auth for M365/Azure:** No automated auth. You can try `m365 util accesstoken get` or `az account get-access-token` (use `-h` for options) but the user may not be signed in.

**Cleanup:** After testing, stop the proxy using `devproxy stop` (preferred, cross-platform). If you started it with `dotnet run`, you can also stop it with `Ctrl+C` in that terminal; as a last resort, terminate the process using OS-specific tools (for example, `kill <PID>` on Unix-like systems or Task Manager on Windows). Clean up any generated log files (`devproxy-*.log`).

## Reference (Architecture)

- **Main entry point:** `DevProxy/Program.cs` — sets up web host, configuration, logging, plugins, and starts proxy engine
- **Proxy Engine:** `DevProxy/Proxy/ProxyEngine.cs` — network interception, request/response handling, plugin invocation
- **Plugins:** Implement `IPlugin` interface (`DevProxy.Abstractions/Plugins/IPlugin.cs`). Base classes: `BasePlugin`, `BaseReportingPlugin` in `DevProxy.Abstractions/Plugins/`
- **CLI Commands:** `DevProxy/Commands/` — built with System.CommandLine, registered via `DevProxyCommand.cs`
- **Configuration:** `DevProxy/config/devproxyrc.json` and other JSON files in `config/`
- **macOS scripts:** `trust-cert.sh`, `remove-cert.sh`, `toggle-proxy.sh` in `DevProxy/`

## Resources

- [Official documentation](https://aka.ms/devproxy)
- [YouTube channel](https://youtube.com/@devproxy)
