# Test LLM Applications

Mock language model responses, simulate LLM failures and rate limiting, monitor token usage, and test MCP servers using Dev Proxy's AI-focused plugins.

## Plugin Selection Guide

| Goal | Plugin | Type |
|------|--------|------|
| Mock OpenAI/Azure OpenAI responses locally | `OpenAIMockResponsePlugin` | Intercepting |
| Simulate LLM failures (hallucinations, bias, etc.) | `LanguageModelFailurePlugin` | Intercepting |
| Simulate token-based rate limiting | `LanguageModelRateLimitingPlugin` | Intercepting |
| Track LLM usage to OpenTelemetry dashboards | `OpenAITelemetryPlugin` | Intercepting |
| Log LLM usage metrics to CSV | `OpenAIUsageDebuggingPlugin` | Intercepting |
| Mock MCP server (STDIO) responses | `MockStdioResponsePlugin` | STDIO |

## Mock OpenAI and Azure OpenAI Responses

Use `OpenAIMockResponsePlugin` to simulate OpenAI/Azure OpenAI completions and chat completions using a local language model (e.g., Ollama). Develop AI apps without incurring API costs.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "OpenAIMockResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ],
  "urlsToWatch": [
    "https://api.openai.com/*",
    "https://*.openai.azure.com/*"
  ],
  "languageModel": {
    "enabled": true,
    "client": "OpenAI",
    "model": "llama3.2",
    "url": "http://localhost:11434/v1/",
    "cacheResponses": true
  }
}
```

**Prerequisites:** Start a local language model host before Dev Proxy:

```bash
ollama pull llama3.2
ollama serve
```

**Language model configuration options:**

| Option | Description | Default |
|--------|-------------|---------|
| `enabled` | Enable local language model | `false` |
| `client` | Client type: `Ollama` or `OpenAI` | `OpenAI` |
| `model` | Model name | `llama3.2` |
| `url` | Language model endpoint URL | `http://localhost:11434/v1/` |
| `cacheResponses` | Cache responses for identical prompts | `true` |

## Simulate LLM Failures

Use `LanguageModelFailurePlugin` to test how an app handles common LLM failure modes.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "LanguageModelFailurePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "languageModelFailurePlugin"
    }
  ],
  "urlsToWatch": [
    "https://api.openai.com/*",
    "https://*.openai.azure.com/*"
  ],
  "languageModelFailurePlugin": {
    "failures": ["Hallucination", "PlausibleIncorrect"]
  }
}
```

### All 15 Built-in Failure Types

| Failure Type | Description |
|--------------|-------------|
| `Hallucination` | Generates false or made-up information |
| `PlausibleIncorrect` | Provides plausible but incorrect information |
| `BiasStereotyping` | Introduces bias or stereotyping |
| `ContradictoryInformation` | Provides contradictory information |
| `Misinterpretation` | Misinterprets the user's request |
| `FailureFollowInstructions` | Fails to follow specific instructions |
| `IncorrectFormatStyle` | Responds in incorrect format or style |
| `AmbiguityVagueness` | Provides ambiguous or vague responses |
| `CircularReasoning` | Uses circular reasoning |
| `OverconfidenceUncertainty` | Shows overconfidence about uncertain info |
| `Overgeneralization` | Makes overly broad generalizations |
| `OutdatedInformation` | Provides outdated information |
| `OverSpecification` | Provides unnecessarily detailed responses |
| `OverreliancePriorConversation` | Over-relies on previous context |
| `FailureDisclaimHedge` | Uses excessive disclaimers or hedging |

When `failures` is omitted, the plugin randomly selects from all available types.

### Testing Specific Categories

**Hallucination-focused:** `["Hallucination", "PlausibleIncorrect", "ContradictoryInformation"]`

**Safety and bias:** `["BiasStereotyping", "Overgeneralization", "OverconfidenceUncertainty"]`

**Instruction-following:** `["FailureFollowInstructions", "IncorrectFormatStyle", "Misinterpretation"]`

### Custom Failure Types

Create custom failures by adding `.prompty` files in `~appFolder/prompts/`. Name the file `lmfailure_<failure>.prompty` using kebab-case (e.g., `lmfailure_domain-specific-error.prompty`). Reference in config using PascalCase (`DomainSpecificError`).

## Simulate Token-Based Rate Limiting

Use `LanguageModelRateLimitingPlugin` to test token quota handling.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "LanguageModelRateLimitingPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "languageModelRateLimitingPlugin"
    }
  ],
  "urlsToWatch": [
    "https://api.openai.com/*",
    "https://*.openai.azure.com/*"
  ],
  "languageModelRateLimitingPlugin": {
    "promptTokenLimit": 5000,
    "completionTokenLimit": 5000,
    "resetTimeWindowSeconds": 60,
    "whenLimitExceeded": "Throttle"
  }
}
```

| Property | Description | Default |
|----------|-------------|---------|
| `promptTokenLimit` | Max prompt tokens per window | `5000` |
| `completionTokenLimit` | Max completion tokens per window | `5000` |
| `resetTimeWindowSeconds` | Window duration in seconds | `60` |
| `whenLimitExceeded` | `Throttle` or `Custom` | `Throttle` |
| `headerRetryAfter` | Retry-after header name | `retry-after` |
| `customResponseFile` | Custom response file path (when `Custom`) | `token-limit-response.json` |

### Custom Rate Limit Response

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/languagemodelratelimitingplugin.customresponsefile.schema.json",
  "statusCode": 429,
  "headers": [
    { "name": "retry-after", "value": "@dynamic" },
    { "name": "content-type", "value": "application/json" }
  ],
  "body": {
    "error": {
      "message": "Token quota exceeded. Please wait.",
      "type": "insufficient_quota",
      "code": "token_quota_exceeded"
    }
  }
}
```

Use `@dynamic` for the retry-after header to auto-calculate seconds until reset.

### Scenario Configs

**Tight limits (stress testing):** `promptTokenLimit: 500, completionTokenLimit: 250, resetTimeWindowSeconds: 30`

**Realistic production simulation:** `promptTokenLimit: 80000, completionTokenLimit: 40000, resetTimeWindowSeconds: 60`

## Monitor LLM Usage with OpenTelemetry

Use `OpenAITelemetryPlugin` to send usage telemetry to OpenTelemetry-compatible dashboards (.NET Aspire, OpenLIT).

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "OpenAITelemetryPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "openAITelemetryPlugin"
    }
  ],
  "urlsToWatch": [
    "https://api.openai.com/*",
    "https://*.openai.azure.com/*"
  ],
  "openAITelemetryPlugin": {
    "application": "my-ai-app",
    "environment": "development",
    "exporterEndpoint": "http://localhost:4318",
    "includeCosts": true,
    "pricesFile": "prices.json",
    "includePrompt": true,
    "includeCompletion": true
  }
}
```

| Property | Description | Default |
|----------|-------------|---------|
| `application` | App name for grouping telemetry | `default` |
| `currency` | Currency for cost display | `USD` |
| `environment` | Environment label | `development` |
| `exporterEndpoint` | OpenTelemetry HTTP Protobuf endpoint | `http://localhost:4318` |
| `includeCompletion` | Include completion text | `true` |
| `includeCosts` | Include cost metrics (requires prices file) | `false` |
| `includePrompt` | Include prompt text | `true` |
| `pricesFile` | Path to prices JSON file | `null` |

### Prices File

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/openaitelemetryplugin.pricesfile.schema.json",
  "prices": {
    "gpt-4": { "input": 0.03, "output": 0.06 },
    "gpt-4-turbo": { "input": 0.01, "output": 0.03 },
    "gpt-3.5-turbo": { "input": 0.0015, "output": 0.002 },
    "gpt-4o": { "input": 0.005, "output": 0.015 },
    "gpt-4o-mini": { "input": 0.00015, "output": 0.0006 }
  }
}
```

Prices are per million tokens. Model name must match the model name in API requests.

### Metrics Logged

| Metric | Description |
|--------|-------------|
| `gen_ai.client.token.usage` | Token count per request/response |
| `gen_ai.usage.cost` | Cost per request |
| `gen_ai.usage.total_cost` | Cumulative cost across session |

### Compatible Dashboards

- **.NET Aspire Dashboard:** `docker run -p 18888:18888 -p 4318:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest`
- **OpenLIT:** Follow [OpenLIT setup](https://openlit.io/)
- Any OpenTelemetry-compatible collector accepting HTTP Protobuf on port 4318

## Debug LLM Usage with CSV Logs

Use `OpenAIUsageDebuggingPlugin` for offline analysis. No configuration needed.

```json
{
  "plugins": [
    {
      "name": "OpenAIUsageDebuggingPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ]
}
```

Output file: `devproxy_llmusage_<timestamp>.csv`

**CSV columns:** `time`, `status`, `retry-after`, `policy`, `prompt tokens`, `completion tokens`, `cached tokens`, `total tokens`, `remaining tokens`, `remaining requests`

Both `OpenAITelemetryPlugin` and `OpenAIUsageDebuggingPlugin` can run simultaneously.

## Test MCP Servers

Use `MockStdioResponsePlugin` with the `stdio` command:

```bash
devproxy stdio --config-file .devproxy/devproxyrc.json -- node my-mcp-server.js
```

See the mocking reference (`references/mock-api-responses.md`) for STDIO mock file format and placeholders.

## Combining LLM Testing Plugins

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "LanguageModelFailurePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "languageModelFailurePlugin" },
    { "name": "LanguageModelRateLimitingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "languageModelRateLimitingPlugin" },
    { "name": "OpenAITelemetryPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "openAITelemetryPlugin" },
    { "name": "OpenAIUsageDebuggingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://api.openai.com/*", "https://*.openai.azure.com/*"],
  "rate": 50,
  "languageModelFailurePlugin": { "failures": ["Hallucination", "FailureFollowInstructions"] },
  "languageModelRateLimitingPlugin": { "promptTokenLimit": 10000, "completionTokenLimit": 5000, "resetTimeWindowSeconds": 60, "whenLimitExceeded": "Throttle" },
  "openAITelemetryPlugin": { "application": "my-ai-app", "environment": "test", "includeCosts": true, "pricesFile": "prices.json" }
}
```

This config: randomly injects LLM failures (50% of requests), enforces token limits, streams telemetry to a dashboard, and logs CSV metrics — all simultaneously.

### Local Development with Mocked OpenAI

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "OpenAIMockResponsePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "OpenAIUsageDebuggingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://api.openai.com/*", "https://*.openai.azure.com/*"],
  "languageModel": { "enabled": true, "model": "llama3.2", "url": "http://localhost:11434/v1/", "cacheResponses": true }
}
```

### MCP Server Testing with Latency

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latencyPlugin" },
    { "name": "MockStdioResponsePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "mockStdioResponsePlugin" }
  ],
  "mockStdioResponsePlugin": { "mocksFile": "mcp-mocks.json", "blockUnmockedRequests": true },
  "latencyPlugin": { "minMs": 200, "maxMs": 1000 }
}
```

Run: `devproxy stdio --config-file .devproxy/devproxyrc.json -- node my-mcp-server.js`
