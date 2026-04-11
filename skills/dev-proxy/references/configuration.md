# Configuration & Plugin Architecture

Dev Proxy uses a plugin-based architecture configured through a JSON configuration file. This reference covers configuration structure, plugin types, URL matching, and proxy settings.

## Configuration File Structure

Store all Dev Proxy files in a `.devproxy` folder in the workspace. The configuration file is named `devproxyrc.json` (or `devproxyrc.jsonc` for comments).

A configuration file follows a specific property order: `$schema`, then `plugins` array, then `urlsToWatch`, then plugin config sections, then general settings.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "RetryAfterPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    },
    {
      "name": "GenericRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "genericRandomErrorPlugin"
    }
  ],
  "urlsToWatch": [
    "https://jsonplaceholder.typicode.com/*"
  ],
  "genericRandomErrorPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/genericrandomerrorplugin.schema.json",
    "errorsFile": "devproxy-errors.json",
    "rate": 50
  },
  "logLevel": "information"
}
```

### Key Rules

- Store Dev Proxy files in a `.devproxy` folder in the workspace root.
- The schema version must match the installed Dev Proxy version. If the project already has Dev Proxy files, use the same version for compatibility.
- Include `$schema` in both the root config and each plugin config section for validation.
- The `pluginPath` is always `~appFolder/plugins/DevProxy.Plugins.dll` — `~appFolder` resolves to the Dev Proxy installation directory.
- The `configSection` value can be any string. Use different, descriptive names for multiple instances of the same plugin.
- File paths in Dev Proxy configuration files are always relative to the file where they are defined.
- Configuration hot-reloads on file save (v2.1.0+) — no restart needed. Works for both the main config and plugin-specific files.

## Plugin Architecture

Dev Proxy has four plugin types:

### 1. Intercepting Plugins

Intercept HTTP requests/responses. Subscribe to `BeforeRequest`, `BeforeResponse`, `AfterResponse` events. **Order matters** — processed in the sequence listed in the config.

### 2. STDIO Plugins

Intercept stdin/stdout/stderr when using `devproxy stdio`. Used for MCP server testing. Supported: `MockStdioResponsePlugin`, `DevToolsPlugin`, `LatencyPlugin`.

### 3. Reporting Plugins

Run after recording stops via `AfterRecordingStop`. Analyze recorded data and generate report objects stored in memory.

### 4. Reporters

Convert report objects into files (Markdown, JSON, plain text). Available: `MarkdownReporter`, `JsonReporter`, `PlainTextReporter`.

> **CRITICAL: Reporters MUST be listed AFTER reporting plugins in the config.**

## Plugin Ordering

1. `RetryAfterPlugin` first (when simulating throttling)
2. `LatencyPlugin` before other plugins
3. `AuthPlugin` before mock plugins
4. Response-simulating plugins (mocks, errors)
5. Reporting plugins
6. Reporters last

Example:

```json
{
  "plugins": [
    { "name": "RetryAfterPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "GraphRandomErrorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "graphRandomErrorPlugin" },
    { "name": "ExecutionSummaryPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "MarkdownReporter", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ]
}
```

## URL Matching

The `urlsToWatch` array determines which requests Dev Proxy intercepts. Use `*` as a wildcard (converted to `.*` regex at runtime).

```json
"urlsToWatch": [
  "https://api.contoso.com/v2/*",
  "https://api.contoso.com/*",
  "https://graph.microsoft.com/v1.0/*",
  "https://graph.microsoft.com/beta/*"
]
```

### URL Matching Rules

- **Order matters:** most specific URLs first.
- **Exclude URLs:** prepend with `!` (e.g., `"!https://api.contoso.com/health"`).
- Plugins inherit global `urlsToWatch`. Only define plugin-specific `urlsToWatch` to override.
- If a plugin has no `urlsToWatch`, the global `urlsToWatch` must have at least one entry.

Plugin-specific override:

```json
{
  "name": "GenericRandomErrorPlugin",
  "enabled": true,
  "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
  "configSection": "errorsContosoApi",
  "urlsToWatch": ["https://api.contoso.com/*"]
}
```

## Key Proxy Settings

| Setting | Default | CLI flag | Description |
|---------|---------|----------|-------------|
| `port` | `8000` | `-p` | Proxy listening port |
| `apiPort` | `8897` | `--api-port` | Dev Proxy API port |
| `rate` | `50` | `-f` | Failure rate (0-100) |
| `logLevel` | `information` | `--log-level` | Log verbosity |
| `record` | off | `--record` | Start in recording mode |
| `watchPids` | — | `--watch-pids` | Only intercept from these PIDs |
| `watchProcessNames` | — | `--watch-process-names` | Only intercept from these processes |
| `asSystemProxy` | `true` | `--as-system-proxy` | Register as system proxy |

## Local Language Model

Enable a local LLM (e.g., Ollama) to improve AI-powered plugins:

```json
{
  "languageModel": {
    "enabled": true,
    "model": "llama3.2",
    "url": "http://localhost:11434/v1/",
    "client": "OpenAI",
    "cacheResponses": true
  }
}
```

Used by: `OpenAIMockResponsePlugin`, `OpenApiSpecGeneratorPlugin`, `TypeSpecGeneratorPlugin`.

## Common Commands

| Command | Description |
|---------|-------------|
| `devproxy` | Start with default/local config |
| `devproxy --config-file path.json` | Start with specific config |
| `devproxy config new` | Create new config file |
| `devproxy config new myconfig` | Create named config file |
| `devproxy config get <id>` | Download preset from gallery |
| `devproxy cert ensure` | Ensure SSL cert exists and is trusted |
| `devproxy jwt create` | Create a JWT for testing |
| `devproxy stdio <command>` | Proxy STDIO communication |

## Process Filtering

Limit interception to specific processes:

```bash
devproxy --watch-process-names msedge node
devproxy --watch-pids 1234 5678
```

Or filter by request headers:

```json
"filterByHeaders": [{ "name": "x-custom-header", "value": "" }]
```

## Microsoft Graph Guidance Plugins

| Plugin | Description |
|--------|-------------|
| `GraphBetaSupportGuidancePlugin` | Warns when beta endpoints are used |
| `GraphClientRequestIdGuidancePlugin` | Recommends client-request-id header |
| `GraphConnectorGuidancePlugin` | Graph connector guidance |
| `GraphSdkGuidancePlugin` | Recommends official SDKs |
| `GraphSelectGuidancePlugin` | Warns when $select is missing |
| `ODSPSearchGuidancePlugin` | Warns about deprecated ODSP search APIs |
| `ODataPagingGuidancePlugin` | OData paging guidance |
| `CachingGuidancePlugin` | Warns about repeated identical requests |

None of these have configuration properties.

## Other Utility Plugins

### RewritePlugin

Rewrites request URLs using regex capture groups.

| Property | Default | Description |
|----------|---------|-------------|
| `rewritesFile` | `rewrites.json` | Path to rewrite rules file |

### MockRequestPlugin

Issues outbound web requests from Dev Proxy (e.g., webhook simulation). Triggered by pressing `w`.

| Property | Default | Description |
|----------|---------|-------------|
| `mockFile` | `mock-request.json` | Path to mock request file |

### DevToolsPlugin

Exposes Dev Proxy activity in Chrome DevTools (HTTP and STDIO).

| Property | Default | Description |
|----------|---------|-------------|
| `preferredBrowser` | `Edge` | `Edge`, `EdgeDev`, or `Chrome` |
