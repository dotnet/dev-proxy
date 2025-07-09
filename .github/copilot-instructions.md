# Copilot Instructions for Dev Proxy

## Overview
Dev Proxy is a cross-platform .NET API simulator and proxy for testing how applications handle API failures, rate limits, latency, and more. It is highly extensible via a plugin architecture and is configured using JSON files. The project is organized as a multi-project .NET solution.

**Key technologies:**
- Uses a fork of Titanium.Web.Proxy: [svrooij/unobtanium-web-proxy](https://github.com/svrooij/unobtanium-web-proxy)
- CLI built with System.CommandLine
- .NET Dependency Injection is used throughout

## Architecture
- **Main entry point:** `DevProxy/Program.cs` sets up the web host, loads configuration, logging, and plugins, and starts the proxy engine.
- **Proxy Engine:** `DevProxy/Proxy/ProxyEngine.cs` manages network interception, request/response handling, plugin invocation, and system proxy integration.
- **Plugins:** Implement the `IPlugin` interface (`DevProxy.Abstractions/Plugins/IPlugin.cs`). For most plugins, inherit from `BasePlugin` or `BaseReportingPlugin` in `DevProxy.Abstractions/Plugins/`. Plugins are loaded dynamically based on configuration in `devproxyrc.json` or similar config files. Plugins can:
  - Intercept and modify requests/responses
  - Add custom commands/options to the CLI (via System.CommandLine)
  - React to events (recording, logging, etc.)
- **Configuration:**
  - Main config: `DevProxy/config/devproxyrc.json` (or other JSON files in `config/`)
  - Plugins and URLs to watch are defined here. See `m365.json` for examples.
- **Commands:**
  - CLI commands are defined in `DevProxy/Commands/` and registered via `DevProxyCommand.cs` (using System.CommandLine).
  - Certificate management: `cert ensure` and `cert remove` (see `CertCommand.cs`).
- **Cross-platform support:**
  - macOS-specific scripts: `trust-cert.sh`, `remove-cert.sh`, `toggle-proxy.sh` in `DevProxy/`.

## Dev Proxy MCP Server
- The Dev Proxy MCP server exposes access to Dev Proxy documentation and JSON schemas, making it invaluable for both contributors and users.
- Use the MCP server to programmatically retrieve up-to-date docs and schema definitions for config and plugins.
- See: [@devproxy/mcp on npm](https://www.npmjs.com/package/@devproxy/mcp)

## Developer Workflows
- **Build:** Use the VS Code task `build` or run `dotnet build DevProxy.sln`.
- **Run:** Use the VS Code task `watch` or run `dotnet watch run --project DevProxy.sln`.
- **Publish:** Use the VS Code task `publish` or run `dotnet publish DevProxy.sln`.
- **Debug:** Attach to the running process or use `dotnet watch run` for hot reload.
- **Test:** (No test project detected; add tests in `tests/` if needed.)

## Project Conventions
- **Plugin loading:** Plugins must be registered in the config file and implement `IPlugin` (preferably via `BasePlugin` or `BaseReportingPlugin`). Use the `PluginServiceExtensions` for registration logic.
- **URL matching:** URLs to watch are defined as wildcards in config and converted to regexes at runtime.
- **Logging:** Uses Microsoft.Extensions.Logging. Log levels and output are configurable.
- **Configuration tokens:** Paths in config can use `~appFolder` for resolution.
- **Hotkeys:** When running interactively, hotkeys are available (see console output for details).
- **Schema validation:** Config files reference a `$schema` for validation and versioning.
- **CLI:** All CLI commands and options are built using System.CommandLine.
- **Dependency Injection:** All services, plugins, and commands are registered and resolved via .NET DI.

## Key Files & Directories
- `DevProxy/Program.cs` — Main entry
- `DevProxy/Proxy/ProxyEngine.cs` — Core proxy logic
- `DevProxy/Commands/` — CLI commands (System.CommandLine)
- `DevProxy/Plugins/` — Plugin loader and helpers
- `DevProxy.Abstractions/Plugins/IPlugin.cs` — Plugin interface
- `DevProxy.Abstractions/Plugins/BasePlugin.cs`, `BaseReportingPlugin.cs` — Plugin base classes
- `DevProxy/config/` — Example and default configs
- `media/` — Branding assets
- `scripts/` — Local build/setup scripts

## External Dependencies
- [svrooij/unobtanium-web-proxy](https://github.com/svrooij/unobtanium-web-proxy) — Core proxy engine (fork of Titanium.Web.Proxy)
- Microsoft.Extensions.* — Logging, configuration, DI
- System.CommandLine — CLI framework

## Examples
- To add a new plugin, inherit from `BasePlugin` or `BaseReportingPlugin`, implement `IPlugin`, and register it in the config file under `plugins`.
- To add a new CLI command, implement a `Command` in `DevProxy/Commands/` and register it in `DevProxyCommand.cs`.

## Resources
- [Official documentation](https://aka.ms/devproxy)
- [YouTube channel](https://youtube.com/@devproxy)

---

*Update this file as project conventions evolve. For questions, see the README or open an issue.*
