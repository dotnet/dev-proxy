# Dev Proxy Plugin Catalog

Complete reference of all Dev Proxy plugins organized by category with configuration properties.

## Intercepting Plugins

### MockResponsePlugin

Returns predefined mock HTTP responses for any API.

| Property | Default | Description |
|----------|---------|-------------|
| `mocksFile` | `mocks.json` | Path to mock responses file |
| `blockUnmockedRequests` | `false` | Return 502 for unmocked requests |

CLI: `-n, --no-mocks` to disable, `--mocks-file` to override path.

### CrudApiPlugin

Simulates a full CRUD API with in-memory data store.

| Property | Description |
|----------|-------------|
| `apiFile` | Path to the API definition file |

### GenericRandomErrorPlugin

Randomly fails requests with errors from a configured file.

| Property | Default | Description |
|----------|---------|-------------|
| `errorsFile` | — | Path to error responses file (required) |
| `rate` | `50` | Failure percentage (0-100) |
| `retryAfterInSeconds` | `5` | Retry-After header value |

CLI: `-f, --failure-rate`

### GraphRandomErrorPlugin

Randomly fails Microsoft Graph requests with Graph-specific error responses.

| Property | Default | Description |
|----------|---------|-------------|
| `allowedErrors` | `[429, 500, 502, 503, 504, 507]` | Error codes to simulate |
| `rate` | `50` | Failure percentage |
| `retryAfterInSeconds` | `5` | Retry-After header value |

CLI: `-a, --allowed-errors`, `-f, --failure-rate`

### LatencyPlugin

Adds random delay to responses (HTTP and STDIO).

| Property | Default | Description |
|----------|---------|-------------|
| `minMs` | `0` | Minimum delay in milliseconds |
| `maxMs` | `5000` | Maximum delay in milliseconds |

### RateLimitingPlugin

Simulates API rate limiting with configurable headers and thresholds.

| Property | Default | Description |
|----------|---------|-------------|
| `headerLimit` | `RateLimit-Limit` | Limit header name |
| `headerRemaining` | `RateLimit-Remaining` | Remaining header name |
| `headerReset` | `RateLimit-Reset` | Reset header name |
| `headerRetryAfter` | `Retry-After` | Retry-After header name |
| `costPerRequest` | `2` | Resources consumed per request |
| `resetTimeWindowSeconds` | `60` | Time window for reset |
| `warningThresholdPercent` | `80` | Warning threshold percentage |
| `rateLimit` | `120` | Total resources per window |
| `whenLimitExceeded` | `Throttle` | `Throttle` or `Custom` |
| `resetFormat` | `SecondsLeft` | `SecondsLeft` or `UtcEpochSeconds` |
| `customResponseFile` | `rate-limit-response.json` | Custom response when limit exceeded |

### RetryAfterPlugin

Simulates Retry-After header behavior. Warns when requests arrive before retry period elapses. **Must be listed BEFORE error plugins.** No configuration properties.

### AuthPlugin

Simulates API key or OAuth2 authentication/authorization.

| Property | Description |
|----------|-------------|
| `type` | `apiKey` or `oauth2` |
| `apiKey` | API key config: `parameters[]` (in: header/query/cookie, name), `allowedKeys[]` |
| `oauth2` | OAuth2 config: `metadataUrl`, `allowedApplications[]`, `allowedAudiences[]`, `issuer`, `roles[]`, `scopes[]`, `validateLifetime`, `validateSigningKey` |

### OpenAIMockResponsePlugin

Simulates OpenAI/Azure OpenAI responses using a local language model. Requires `languageModel` configuration. No plugin-specific config properties.

### LanguageModelFailurePlugin

Simulates LLM failure scenarios (hallucinations, bias, etc.).

| Property | Default | Description |
|----------|---------|-------------|
| `failures` | all types | Array of failure type names to simulate |

15 built-in types: `Hallucination`, `PlausibleIncorrect`, `BiasStereotyping`, `CircularReasoning`, `ContradictoryInformation`, `AmbiguityVagueness`, `FailureDisclaimHedge`, `FailureFollowInstructions`, `IncorrectFormatStyle`, `Misinterpretation`, `OutdatedInformation`, `OverSpecification`, `OverconfidenceUncertainty`, `Overgeneralization`, `OverreliancePriorConversation`.

### LanguageModelRateLimitingPlugin

Simulates token-based rate limiting for LLM APIs.

| Property | Default | Description |
|----------|---------|-------------|
| `promptTokenLimit` | `5000` | Max prompt tokens per window |
| `completionTokenLimit` | `5000` | Max completion tokens per window |
| `resetTimeWindowSeconds` | `60` | Reset window in seconds |
| `whenLimitExceeded` | `Throttle` | `Throttle` or `Custom` |
| `headerRetryAfter` | `retry-after` | Retry-After header name |
| `customResponseFile` | `token-limit-response.json` | Custom response file |

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

### MockStdioResponsePlugin

Mocks STDIO responses for MCP servers and STDIO-based apps.

| Property | Default | Description |
|----------|---------|-------------|
| `mocksFile` | `stdio-mocks.json` | Path to STDIO mocks file |
| `blockUnmockedRequests` | `false` | Block unmatched stdin |

CLI: `--no-stdio-mocks`, `--stdio-mocks-file`

### OpenAITelemetryPlugin

Logs OpenAI telemetry data to OpenTelemetry-compatible dashboards.

| Property | Default | Description |
|----------|---------|-------------|
| `application` | `default` | Application name for grouping |
| `currency` | `USD` | Currency for cost display |
| `environment` | `development` | Environment name |
| `exporterEndpoint` | `http://localhost:4318` | OpenTelemetry endpoint |
| `includeCompletion` | `true` | Log completion text |
| `includeCosts` | `false` | Log cost metrics |
| `includePrompt` | `true` | Log prompt text |
| `pricesFile` | `null` | Path to model prices file |

### OpenAIUsageDebuggingPlugin

Logs OpenAI API usage metrics to CSV. No configuration properties.

## Reporting Plugins

### ExecutionSummaryPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `groupBy` | `url` | `url` or `messageType` |

CLI: `--summary-group-by`

### OpenApiSpecGeneratorPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `includeOptionsRequests` | `false` | Include OPTIONS requests |
| `ignoreResponseTypes` | `false` | Ignore response types |
| `specVersion` | `v3_0` | `v2_0` or `v3_0` |
| `specFormat` | `Json` | `Json` or `Yaml` |
| `includeParameters` | `[]` | Query string params to include |

### TypeSpecGeneratorPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `ignoreResponseTypes` | `false` | Ignore response types |

### HttpFileGeneratorPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `includeOptionsRequests` | `false` | Include OPTIONS requests |

### HarGeneratorPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `includeSensitiveInformation` | `false` | Include auth headers |
| `includeResponse` | `false` | Include response bodies |

### GraphMinimalPermissionsPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `type` | `Delegated` | `Delegated` or `Application` |

### GraphMinimalPermissionsGuidancePlugin

| Property | Default | Description |
|----------|---------|-------------|
| `permissionsToExclude` | `["profile", "openid", "offline_access", "email"]` | Scopes to ignore |

### UrlDiscoveryPlugin

No configuration properties.

### MockGeneratorPlugin

No configuration properties.

### ApiCenterOnboardingPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `subscriptionId` | — | Azure subscription ID |
| `resourceGroupName` | — | Resource group name |
| `serviceName` | — | API Center instance name |
| `workspace` | `default` | API Center workspace |
| `createApicEntryForNewApis` | `true` | Auto-register shadow APIs |

Supports `@ENV_VAR` syntax.

### ApiCenterProductionVersionPlugin

| Property | Default | Description |
|----------|---------|-------------|
| `subscriptionId` | — | Azure subscription ID |
| `resourceGroupName` | — | Resource group name |
| `serviceName` | — | API Center instance name |
| `workspace` | `default` | API Center workspace |

Supports `@ENV_VAR` syntax.

### ApiCenterMinimalPermissionsPlugin

Same Azure config properties as above.

## Microsoft Graph Guidance Plugins

| Plugin | Description |
|--------|-------------|
| `GraphBetaSupportGuidancePlugin` | Warns when beta endpoints are used |
| `GraphClientRequestIdGuidancePlugin` | Recommends client-request-id header |
| `GraphConnectorGuidancePlugin` | Graph connector guidance |
| `GraphSdkGuidancePlugin` | Recommends official SDKs |
| `GraphSelectGuidancePlugin` | Warns when $select is missing |
| `GraphMockResponsePlugin` | Mocks Graph responses including batch |
| `ODSPSearchGuidancePlugin` | Warns about deprecated ODSP search APIs |
| `ODataPagingGuidancePlugin` | OData paging guidance |
| `CachingGuidancePlugin` | Warns about repeated identical requests |

None of these have configuration properties.

## Reporters

All reporters must be listed AFTER reporting plugins. No configuration properties.

| Reporter | Output Format |
|----------|--------------|
| `JsonReporter` | JSON |
| `MarkdownReporter` | Markdown |
| `PlainTextReporter` | Plain text |
