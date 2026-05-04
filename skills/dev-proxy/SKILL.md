---
name: dev-proxy
description: "Simulate API failures, mock responses, test rate limiting, and analyze API traffic using Dev Proxy's plugin-based proxy engine. WHEN: 'mock API responses', 'simulate API errors', 'test rate limiting', 'test error handling', 'mock OpenAI responses', 'test AI app', 'analyze API usage', 'configure Dev Proxy', 'install Dev Proxy', 'set up Dev Proxy', 'use Dev Proxy in CI/CD', 'chaos testing for APIs', 'run Dev Proxy in background', 'detached mode', 'run Dev Proxy detached'."
---

# Dev Proxy

Dev Proxy is a command-line API simulator and proxy for testing how applications handle API failures, rate limits, latency, and more. It uses a plugin-based architecture configured through JSON configuration files.

## Prerequisites

Before proceeding, verify that Dev Proxy is installed by running `devproxy --version`. If the command fails or Dev Proxy is not found:

STOP — Read [references/installation.md](references/installation.md) for platform-specific installation instructions (macOS, Windows, Linux), first-run certificate setup, and verification steps.

## Quick Start

Store all Dev Proxy files in `.devproxy/` in the workspace root. Start with:

```bash
devproxy --config-file .devproxy/devproxyrc.json
```

STOP — Before creating or modifying any Dev Proxy configuration, read [references/best-practices.md](references/best-practices.md) for critical rules on file structure, plugin ordering, URL matching, and configuration patterns.

## Scenario Selection

Identify the user's scenario and load the corresponding reference file for detailed instructions.

### Mock API Responses

Return predefined responses, simulate CRUD APIs, mock MCP servers, or simulate authentication.

**Plugins:** `MockResponsePlugin`, `CrudApiPlugin`, `GraphMockResponsePlugin`, `MockStdioResponsePlugin`, `AuthPlugin`

STOP — Read [references/mock-api-responses.md](references/mock-api-responses.md) for mock file structure, matching rules, dynamic responses, CRUD API definitions, STDIO mocks, and auth simulation.

### Test API Resilience

Simulate API failures, rate limits, throttling, and slow responses to verify error handling.

**Plugins:** `GenericRandomErrorPlugin`, `GraphRandomErrorPlugin`, `RateLimitingPlugin`, `RetryAfterPlugin`, `LatencyPlugin`

STOP — Read [references/test-api-resilience.md](references/test-api-resilience.md) for error injection configs, rate limiting setup, throttling simulation, and latency testing.

### Test LLM Applications

Mock OpenAI/Azure OpenAI responses, simulate LLM failures, enforce token-based rate limits, and monitor usage.

**Plugins:** `OpenAIMockResponsePlugin`, `LanguageModelFailurePlugin`, `LanguageModelRateLimitingPlugin`, `OpenAITelemetryPlugin`, `OpenAIUsageDebuggingPlugin`, `MockStdioResponsePlugin`

STOP — Read [references/test-llm-apps.md](references/test-llm-apps.md) for LLM mocking with local models, failure simulation, token rate limiting, telemetry dashboards, and MCP server testing.

### Analyze API Usage

Record traffic and generate reports, specs, and governance insights.

**Plugins:** `OpenApiSpecGeneratorPlugin`, `TypeSpecGeneratorPlugin`, `HttpFileGeneratorPlugin`, `HarGeneratorPlugin`, `ExecutionSummaryPlugin`, `UrlDiscoveryPlugin`, `MockGeneratorPlugin`, `GraphMinimalPermissionsPlugin`, `GraphMinimalPermissionsGuidancePlugin`, `ApiCenterOnboardingPlugin`, `ApiCenterProductionVersionPlugin`

STOP — Read [references/analyze-api-usage.md](references/analyze-api-usage.md) for recording workflow, spec generation, permission analysis, shadow API detection, and reporting.

### CI/CD Integration

Automate API testing in GitHub Actions, Azure Pipelines, or any CI system.

STOP — Read [references/ci-cd-integration.md](references/ci-cd-integration.md) for GitHub Actions setup, Azure Pipelines config, general CI patterns, and pipeline templates.

### Configuration & Plugin Reference

Set up Dev Proxy, understand plugin architecture, URL matching, proxy settings, and detached (background) mode.

STOP — Read [references/configuration.md](references/configuration.md) for config file structure, plugin types and ordering, URL matching rules, proxy settings, detached mode, and process filtering. For the full plugin catalog, read [references/plugin-catalog.md](references/plugin-catalog.md).

## Configuration File Structure

A quick reference. All configuration details are in [references/configuration.md](references/configuration.md).

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "PluginName",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "pluginConfig"
    }
  ],
  "urlsToWatch": ["https://api.contoso.com/*"],
  "pluginConfig": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/pluginname.schema.json"
  }
}
```

### Critical Rules

- Property order: `$schema` → `plugins` → `urlsToWatch` → plugin config sections → general settings
- `pluginPath` is always `~appFolder/plugins/DevProxy.Plugins.dll`
- Schema version must match the installed Dev Proxy version
- Reporter plugins MUST be listed AFTER reporting plugins
- Plugin execution order follows the config array order
- File paths are relative to the config file where they are defined
- Configuration hot-reloads on file save (v2.1.0+)

### Plugin Ordering

1. `RetryAfterPlugin` first (when simulating throttling)
2. `LatencyPlugin` before mock/error plugins
3. `AuthPlugin` before mock plugins
4. Response-simulating plugins (mocks, errors)
5. Reporting plugins
6. Reporters last (`MarkdownReporter`, `JsonReporter`, `PlainTextReporter`)
