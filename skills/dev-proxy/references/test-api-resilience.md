# Test API Resilience

Dev Proxy simulates API failures, rate limits, and slow responses to test how applications handle real-world conditions. Covers error injection, rate limiting, throttling simulation, and latency testing across HTTP and STDIO protocols.

## Error Injection

### GenericRandomErrorPlugin

Randomly fails requests with errors from a configured file. Works with any API.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "GenericRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "genericRandomErrorPlugin"
    }
  ],
  "urlsToWatch": ["https://api.contoso.com/*"],
  "genericRandomErrorPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/genericrandomerrorplugin.schema.json",
    "errorsFile": "errors.json",
    "rate": 50,
    "retryAfterInSeconds": 5
  }
}
```

| Property | Default | Description |
|----------|---------|-------------|
| `errorsFile` | — | Path to error responses file (required) |
| `rate` | `50` | Percentage of requests that fail (0-100) |
| `retryAfterInSeconds` | `5` | Value for Retry-After header |

CLI: `devproxy --failure-rate 80` overrides `rate`.

#### Error File Format

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/genericrandomerrorplugin.schema.json",
  "errors": [
    {
      "statusCode": 429,
      "headers": [
        { "name": "Retry-After", "value": "@dynamic" },
        { "name": "Content-Type", "value": "application/json" }
      ],
      "body": { "error": { "code": "TooManyRequests", "message": "Rate limit exceeded" } },
      "addDynamicRetryAfter": true
    },
    {
      "statusCode": 500,
      "body": { "error": { "code": "InternalServerError", "message": "Something went wrong" } }
    },
    {
      "statusCode": 503,
      "body": { "error": { "code": "ServiceUnavailable", "message": "Service temporarily unavailable" } }
    }
  ]
}
```

When `addDynamicRetryAfter: true`, Dev Proxy auto-calculates the Retry-After value.

#### Generic REST API Error File (Comprehensive)

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/genericrandomerrorplugin.schema.json",
  "errors": [
    { "statusCode": 400, "body": { "error": { "code": "BadRequest", "message": "The request was malformed or contains invalid parameters." } } },
    { "statusCode": 401, "body": { "error": { "code": "Unauthorized", "message": "Authentication required." } } },
    { "statusCode": 403, "body": { "error": { "code": "Forbidden", "message": "You don't have permission to access this resource." } } },
    { "statusCode": 404, "body": { "error": { "code": "NotFound", "message": "The requested resource was not found." } } },
    { "statusCode": 429, "headers": [{ "name": "Retry-After", "value": "@dynamic" }, { "name": "Content-Type", "value": "application/json" }], "body": { "error": { "code": "TooManyRequests", "message": "Rate limit exceeded." } }, "addDynamicRetryAfter": true },
    { "statusCode": 500, "body": { "error": { "code": "InternalServerError", "message": "An internal server error occurred." } } },
    { "statusCode": 502, "body": { "error": { "code": "BadGateway", "message": "Invalid response from upstream server." } } },
    { "statusCode": 503, "body": { "error": { "code": "ServiceUnavailable", "message": "The service is temporarily unavailable." } } },
    { "statusCode": 504, "body": { "error": { "code": "GatewayTimeout", "message": "The server did not receive a timely response from the upstream server." } } }
  ]
}
```

#### OpenAI API Error File

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/genericrandomerrorplugin.schema.json",
  "errors": [
    { "statusCode": 429, "headers": [{ "name": "Retry-After", "value": "@dynamic" }, { "name": "Content-Type", "value": "application/json" }], "body": { "error": { "message": "Rate limit reached for default-gpt-4 in organization org-xxx on tokens per min.", "type": "tokens", "param": null, "code": "rate_limit_exceeded" } }, "addDynamicRetryAfter": true },
    { "statusCode": 429, "body": { "error": { "message": "The engine is currently overloaded, please try again later.", "type": "server_error", "param": null, "code": null } } },
    { "statusCode": 500, "body": { "error": { "message": "The server had an error while processing your request.", "type": "server_error", "param": null, "code": null } } },
    { "statusCode": 503, "body": { "error": { "message": "The server is overloaded or not ready yet.", "type": "server_error", "param": null, "code": null } } }
  ]
}
```

### GraphRandomErrorPlugin

Fails Microsoft Graph requests with Graph-specific error responses. Supports batch requests (returns 424 Failed Dependency for dependent requests).

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "GraphRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "graphRandomErrorPlugin"
    }
  ],
  "urlsToWatch": [
    "https://graph.microsoft.com/v1.0/*",
    "https://graph.microsoft.com/beta/*"
  ],
  "graphRandomErrorPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/graphrandomerrorplugin.schema.json",
    "allowedErrors": [429, 500, 502, 503, 504, 507],
    "rate": 50
  }
}
```

To simulate only throttling: `"allowedErrors": [429]`.

### Multiple APIs with Different Error Configs

Use per-plugin `urlsToWatch` overrides and different `configSection` names:

```json
{
  "plugins": [
    {
      "name": "GenericRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "errorsContosoApi",
      "urlsToWatch": ["https://api.contoso.com/*"]
    },
    {
      "name": "GenericRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "errorsPaymentApi",
      "urlsToWatch": ["https://payments.example.com/*"]
    }
  ],
  "errorsContosoApi": { "errorsFile": "errors-contoso.json" },
  "errorsPaymentApi": { "errorsFile": "errors-payment.json", "rate": 30 }
}
```

## Rate Limiting

### RateLimitingPlugin

Simulates rate-limit behavior with configurable headers, thresholds, and response behavior.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "RateLimitingPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "rateLimiting"
    }
  ],
  "rateLimiting": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/ratelimitingplugin.schema.json",
    "costPerRequest": 2,
    "rateLimit": 120,
    "resetTimeWindowSeconds": 60,
    "warningThresholdPercent": 80,
    "whenLimitExceeded": "Throttle"
  }
}
```

| Property | Default | Description |
|----------|---------|-------------|
| `rateLimit` | `120` | Total resources per window |
| `costPerRequest` | `2` | Resources consumed per request |
| `resetTimeWindowSeconds` | `60` | Reset window in seconds |
| `warningThresholdPercent` | `80` | Start warning at this usage % |
| `whenLimitExceeded` | `Throttle` | `Throttle` (429) or `Custom` |
| `customResponseFile` | `rate-limit-response.json` | Custom response for `Custom` mode |
| `headerLimit` | `RateLimit-Limit` | Limit header name |
| `headerRemaining` | `RateLimit-Remaining` | Remaining header name |
| `headerReset` | `RateLimit-Reset` | Reset header name |
| `headerRetryAfter` | `Retry-After` | Retry-After header name |
| `resetFormat` | `SecondsLeft` | `SecondsLeft` or `UtcEpochSeconds` |

### Custom Rate Limit Response (GitHub-style)

```json
{
  "rateLimiting": {
    "headerLimit": "X-RateLimit-Limit",
    "headerRemaining": "X-RateLimit-Remaining",
    "headerReset": "X-RateLimit-Reset",
    "resetFormat": "UtcEpochSeconds",
    "rateLimit": 60,
    "resetTimeWindowSeconds": 3600,
    "whenLimitExceeded": "Custom",
    "customResponseFile": "github-rate-limit-exceeded.json"
  }
}
```

GitHub-style rate limit response file:

```json
{
  "statusCode": 403,
  "headers": [
    { "name": "Content-Type", "value": "application/json; charset=utf-8" },
    { "name": "X-RateLimit-Limit", "value": "60" },
    { "name": "X-RateLimit-Remaining", "value": "0" },
    { "name": "X-RateLimit-Reset", "value": "@dynamic" }
  ],
  "body": {
    "message": "API rate limit exceeded for user.",
    "documentation_url": "https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"
  }
}
```

## Throttling Simulation

### Microsoft 365 Throttling

Combine `RetryAfterPlugin` + `GraphRandomErrorPlugin` with `allowedErrors: [429]`. **RetryAfterPlugin must be listed FIRST** — it tracks whether the app respects Retry-After headers and warns if requests arrive too early.

```json
{
  "plugins": [
    {
      "name": "RetryAfterPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    },
    {
      "name": "GraphRandomErrorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "graphRandomErrorPlugin"
    }
  ],
  "urlsToWatch": [
    "https://graph.microsoft.com/v1.0/*",
    "https://graph.microsoft.com/beta/*"
  ],
  "graphRandomErrorPlugin": {
    "allowedErrors": [429]
  }
}
```

## Latency Simulation

### LatencyPlugin

Adds random delay to responses. Works with both HTTP and STDIO.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "LatencyPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "latencyPlugin"
    }
  ],
  "latencyPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/latencyplugin.schema.json",
    "minMs": 200,
    "maxMs": 10000
  }
}
```

## Failure Rate Tuning

- Config: `"rate": 80` in plugin config or root level
- CLI: `devproxy --failure-rate 80`
- `0` = all requests pass through
- `100` = all targeted requests fail
- Config hot-reloads — change the rate without restarting

## Preset Configurations

Load built-in presets instead of creating configs from scratch:

```bash
devproxy --config-file "~appFolder/config/microsoft-graph-rate-limiting.json"
devproxy config get openai-throttling
```

## Complete Recipes

### REST API with Errors + Rate Limiting + Latency

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latencyPlugin" },
    { "name": "RateLimitingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "rateLimiting" },
    { "name": "GenericRandomErrorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "genericRandomErrorPlugin" }
  ],
  "urlsToWatch": ["https://api.contoso.com/*"],
  "latencyPlugin": { "minMs": 100, "maxMs": 3000 },
  "rateLimiting": { "rateLimit": 60, "costPerRequest": 1, "resetTimeWindowSeconds": 60 },
  "genericRandomErrorPlugin": { "errorsFile": "errors.json", "rate": 30 }
}
```

### Microsoft Graph Throttling with Reporting

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    { "name": "RetryAfterPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "GraphRandomErrorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "graphRandomErrorPlugin" },
    { "name": "ExecutionSummaryPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "MarkdownReporter", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://graph.microsoft.com/v1.0/*", "https://graph.microsoft.com/beta/*"],
  "graphRandomErrorPlugin": { "allowedErrors": [429] },
  "rate": 80
}
```
