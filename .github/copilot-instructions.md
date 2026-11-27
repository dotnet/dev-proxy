# Copilot Instructions for Dev Proxy

## Overview

Dev Proxy is a cross-platform .NET API simulator and proxy for testing how applications handle API failures, rate limits, latency, and more.

## Critical (Non-Obvious)

- **Proxy engine:** Built on [ASP.NET Core Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel) (a custom forward-proxy + MITM engine; see `DevProxy.Proxy.Kestrel`)
- **Config tokens:** Paths in config files can use `~appFolder` and `~dataFolder` tokens, and a leading `~/` (or `~\` on Windows) expands to the home directory
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
- Hot reload (v2.1.0+): config changes are detected and the proxy restarts automatically to apply them

## Plugin Development

To create a new plugin:
1. Implement the `IPlugin` interface (`DevProxy.Abstractions/Plugins/IPlugin.cs`) either directly or by inheriting from `BasePlugin` / `BaseReportingPlugin`. Optionally implement `IStdioPlugin` for stdio-based communication.
2. Implement it as a **public class** in a class library project.
3. Implement the required interface members and override any relevant base class methods/events for your use case.
4. If the plugin has a configuration class, add `schemas/v<current version>/<lowercased plugin name>.schema.json` matching it (properties, types, defaults). `PluginSchemaConsistencyTests` in `DevProxy.Tests` fails on mismatches.

## Code Conventions

Path-specific rules live in `.github/instructions/` (CLI commands, JSON schemas).

- **Build before review:** `dotnet restore DevProxy.sln --locked-mode`, `dotnet build DevProxy.sln --no-restore` (warnings are errors) and `dotnet test DevProxy.sln` must pass before you request review.
- **Lock files:** only change `packages.lock.json` when you change package references (then run `dotnet restore DevProxy.sln --force-evaluate`). Revert lock file changes that a restore produced without a dependency change.
- **No direct console output** in plugins, abstractions, the proxy engine or CLI commands; log through `ILogger`. Enforced by analyzer `RS0030` with `BannedSymbols.txt` (scope in `.editorconfig`).
- **Reuse, don't duplicate:** search for existing utilities (e.g. `DevProxy.Abstractions/Utils/ProxyUtils.cs`) and global options before adding new ones.
- **Minimal visibility:** make helpers `internal` or `private` unless another assembly needs them. Plugin classes and the plugin API in `DevProxy.Abstractions` are the exceptions that must be `public`.
- **Focused, clear changes:** keep a PR to one issue without unrelated refactoring; give members names that state their purpose and put helpers in a class that matches their domain (e.g. nothing unrelated to Graph in `GraphUtils`).

## Commits & Releases

- **Commit messages:** Clear, succinct, and reference the issue they close when applicable
- **Versioning:** We follow semver. Use the `upgrade-devproxy-version` skill for version bumps
- **Release:** After release builds, homebrew is updated manually. Use the `publish-to-winget` skill for winget submissions
- **Pull requests:** Build and run tests before opening a PR. Use the `pr-review-loop` skill to open it and drive it through Copilot review. Never merge it yourself

## Testing Code Changes

When testing code, features, or changes, **always launch the proxy in detached mode** with these settings to avoid conflicts:

```bash
dotnet run --project DevProxy -- --detach --as-system-proxy false --port 0 --api-port 0
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
- **Dev Container Feature:** `features/src/dev-proxy` (published as `ghcr.io/dotnet/dev-proxy/dev-proxy`), tests in `features/test/dev-proxy`. Run tests from `features/` with `npx @devcontainers/cli features test -f dev-proxy .`. Bump the version in `devcontainer-feature.json` to publish a new Feature version

## Resources

- [Official documentation](https://aka.ms/devproxy)
- [YouTube channel](https://youtube.com/@devproxy)
