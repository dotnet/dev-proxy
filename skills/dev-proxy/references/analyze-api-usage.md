# Analyze API Usage

Record API traffic, analyze usage patterns, and generate reports, specs, and governance insights using Dev Proxy's reporting and analysis plugins.

## Recording Workflow

Dev Proxy operates in two phases: intercepting (live) and reporting (after recording). To analyze API usage, record traffic first, then stop recording to trigger reporting plugins.

### Start and Stop Recording

1. Start Dev Proxy: `devproxy --config-file .devproxy/devproxyrc.json`
2. Press `R` to start recording
3. Exercise the application (run tests, use the UI, etc.)
4. Press `S` to stop recording — reporting plugins process the captured traffic
5. Reports appear in the current working directory

For CI/CD, use the Dev Proxy API instead of keyboard shortcuts:

```bash
# Start recording
curl -X POST http://localhost:8897/proxy \
  -H "Content-Type: application/json" \
  -d '{"recording": true}'

# Stop recording
curl -X POST http://localhost:8897/proxy \
  -H "Content-Type: application/json" \
  -d '{"recording": false}'
```

Use `--record` to start recording automatically when Dev Proxy starts.

## Plugin Architecture for Analysis

Reporting plugins process captured traffic AFTER recording stops. Reporter plugins format and write reports to files. **Reporter plugins must always come AFTER reporting plugins** in the plugins array.

## Generate OpenAPI Specs from Traffic

Use `OpenApiSpecGeneratorPlugin` to reverse-engineer OpenAPI specs from intercepted API traffic.

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    {
      "name": "OpenApiSpecGeneratorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "openApiSpecGeneratorPlugin"
    },
    {
      "name": "PlainTextReporter",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ],
  "urlsToWatch": ["https://api.contoso.com/*"],
  "openApiSpecGeneratorPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/openapispecgeneratorplugin.schema.json",
    "specVersion": "v3_0",
    "specFormat": "Json",
    "includeOptionsRequests": false,
    "includeParameters": ["api-version"]
  }
}
```

| Property | Description | Default |
|----------|-------------|---------|
| `specVersion` | `v2_0` or `v3_0` | `v3_0` |
| `specFormat` | `Json` or `Yaml` | `Json` |
| `includeOptionsRequests` | Include OPTIONS requests | `false` |
| `ignoreResponseTypes` | Skip response type generation | `false` |
| `includeParameters` | Query string parameters to include | `[]` |

For better operation IDs and descriptions, enable a local language model (see below).

## Generate TypeSpec from Traffic

Use `TypeSpecGeneratorPlugin` to generate TypeSpec definitions:

```json
{
  "plugins": [
    {
      "name": "TypeSpecGeneratorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "typeSpecGeneratorPlugin"
    }
  ],
  "typeSpecGeneratorPlugin": {
    "ignoreResponseTypes": false
  }
}
```

## Generate HTTP Files

Use `HttpFileGeneratorPlugin` to create `.http` files from captured requests. The plugin extracts authorization info and replaces them with variables.

```json
{
  "plugins": [
    {
      "name": "HttpFileGeneratorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "httpFileGeneratorPlugin"
    }
  ],
  "httpFileGeneratorPlugin": {
    "includeOptionsRequests": false
  }
}
```

## Export HAR Files

Use `HarGeneratorPlugin` to export HTTP Archive (HAR) files:

```json
{
  "plugins": [
    {
      "name": "HarGeneratorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "harGeneratorPlugin"
    }
  ],
  "harGeneratorPlugin": {
    "includeSensitiveInformation": false,
    "includeResponse": true
  }
}
```

Set `includeSensitiveInformation` to `false` (default) to automatically redact authorization headers, cookies, API keys, and session tokens.

## Execution Summaries

Use `ExecutionSummaryPlugin` to create a summary of all intercepted requests:

```json
{
  "plugins": [
    {
      "name": "ExecutionSummaryPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "executionSummaryPlugin"
    }
  ],
  "executionSummaryPlugin": {
    "groupBy": "url"
  }
}
```

Group by `url` or `messageType`. CLI override: `--summary-group-by`.

## Discover URLs

Use `UrlDiscoveryPlugin` to list all URLs an app calls (useful as a first step before configuring `urlsToWatch`):

```json
{
  "plugins": [
    {
      "name": "UrlDiscoveryPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    },
    {
      "name": "PlainTextReporter",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ],
  "urlsToWatch": ["https://*/*"]
}
```

## Generate Mocks from Traffic

Use `MockGeneratorPlugin` to auto-generate mock files from real API traffic. After recording, the plugin writes mocks to `mocks-yyyyMMddHHmmss.json`:

```json
{
  "plugins": [
    {
      "name": "MockGeneratorPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ]
}
```

## Microsoft Graph Permissions Analysis

### Detect Minimal Permissions

Use `GraphMinimalPermissionsPlugin` to find the minimum permissions needed for recorded Graph API calls:

```json
{
  "plugins": [
    {
      "name": "GraphMinimalPermissionsPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "graphMinimalPermissionsPlugin"
    },
    {
      "name": "MarkdownReporter",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll"
    }
  ],
  "urlsToWatch": [
    "https://graph.microsoft.com/v1.0/*",
    "https://graph.microsoft.com/beta/*"
  ],
  "graphMinimalPermissionsPlugin": {
    "type": "delegated"
  }
}
```

Set `type` to `delegated` or `application` depending on the auth flow.

### Detect Excessive Permissions

Use `GraphMinimalPermissionsGuidancePlugin` to compare JWT token permissions against the minimum required:

```json
{
  "plugins": [
    {
      "name": "GraphMinimalPermissionsGuidancePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "graphMinimalPermissionsGuidancePlugin"
    }
  ],
  "graphMinimalPermissionsGuidancePlugin": {
    "permissionsToExclude": ["profile", "openid", "offline_access", "email"]
  }
}
```

## Azure API Center Governance

### Find Shadow APIs

Use `ApiCenterOnboardingPlugin` to check whether the APIs an app uses are registered in Azure API Center:

```json
{
  "plugins": [
    {
      "name": "ApiCenterOnboardingPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "apiCenterOnboardingPlugin"
    }
  ],
  "apiCenterOnboardingPlugin": {
    "subscriptionId": "aaaa0a0a-bb1b-cc2c-dd3d-eeeeee4e4e4e",
    "resourceGroupName": "resource-group-name",
    "serviceName": "apic-instance",
    "workspace": "default",
    "createApicEntryForNewApis": true
  }
}
```

For CI/CD, use environment variables with `@` prefix:

```json
{
  "apiCenterOnboardingPlugin": {
    "subscriptionId": "@AZURE_SUBSCRIPTION_ID",
    "resourceGroupName": "@AZURE_RESOURCE_GROUP_NAME",
    "serviceName": "@AZURE_APIC_INSTANCE_NAME",
    "workspace": "@AZURE_APIC_WORKSPACE_NAME",
    "createApicEntryForNewApis": false
  }
}
```

The plugin uses Azure credential chain: Environment → Workload Identity → Managed Identity → VS → VS Code → Azure CLI → Azure PowerShell → Azure Developer CLI.

### Check Production API Versions

Use `ApiCenterProductionVersionPlugin` to verify that apps use production-ready API versions:

```json
{
  "plugins": [
    {
      "name": "ApiCenterProductionVersionPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "apiCenterProductionVersionPlugin"
    }
  ],
  "apiCenterProductionVersionPlugin": {
    "subscriptionId": "aaaa0a0a-bb1b-cc2c-dd3d-eeeeee4e4e4e",
    "resourceGroupName": "resource-group-name",
    "serviceName": "apic-instance",
    "workspace": "default"
  }
}
```

## Improving Results with a Local Language Model

`OpenApiSpecGeneratorPlugin` and `TypeSpecGeneratorPlugin` produce better output when a local language model is available:

```json
{
  "languageModel": {
    "enabled": true,
    "client": "OpenAI",
    "model": "llama3.2",
    "url": "http://localhost:11434/v1/"
  }
}
```

Start Ollama before starting Dev Proxy.

## Reporter Plugins

Reporter plugins format report output. Always place them AFTER reporting plugins.

| Reporter | Output |
|----------|--------|
| `PlainTextReporter` | Plain text files |
| `MarkdownReporter` | Markdown files |
| `JsonReporter` | JSON files |

## Complete Analysis Recipes

### Full API Audit with Graph Permissions

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "ExecutionSummaryPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "executionSummaryPlugin" },
    { "name": "GraphMinimalPermissionsPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "graphMinimalPermissionsPlugin" },
    { "name": "GraphMinimalPermissionsGuidancePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "OpenApiSpecGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "openApiSpecGeneratorPlugin" },
    { "name": "MockGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "MarkdownReporter", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://graph.microsoft.com/v1.0/*", "https://graph.microsoft.com/beta/*"],
  "executionSummaryPlugin": { "groupBy": "url" },
  "graphMinimalPermissionsPlugin": { "type": "delegated" },
  "openApiSpecGeneratorPlugin": { "specVersion": "v3_0", "specFormat": "Json" }
}
```

### Shadow API Detection + Production Version Check

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "ApiCenterOnboardingPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "apiCenterOnboardingPlugin" },
    { "name": "ApiCenterProductionVersionPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "apiCenterProductionVersionPlugin" },
    { "name": "MarkdownReporter", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://api.contoso.com/*", "https://*.example.com/*"],
  "apiCenterOnboardingPlugin": { "subscriptionId": "@AZURE_SUBSCRIPTION_ID", "resourceGroupName": "@AZURE_RESOURCE_GROUP_NAME", "serviceName": "@AZURE_APIC_INSTANCE_NAME", "workspace": "default", "createApicEntryForNewApis": false },
  "apiCenterProductionVersionPlugin": { "subscriptionId": "@AZURE_SUBSCRIPTION_ID", "resourceGroupName": "@AZURE_RESOURCE_GROUP_NAME", "serviceName": "@AZURE_APIC_INSTANCE_NAME", "workspace": "default" }
}
```

### URL Discovery + Spec Generation (New App Onboarding)

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.4.0/rc.schema.json",
  "plugins": [
    { "name": "UrlDiscoveryPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "OpenApiSpecGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "openApiSpecGeneratorPlugin" },
    { "name": "HttpFileGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "HarGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "harGeneratorPlugin" },
    { "name": "MockGeneratorPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "PlainTextReporter", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" }
  ],
  "urlsToWatch": ["https://*/*"],
  "openApiSpecGeneratorPlugin": { "specVersion": "v3_0", "specFormat": "Yaml" },
  "harGeneratorPlugin": { "includeSensitiveInformation": false, "includeResponse": true },
  "languageModel": { "enabled": true, "model": "llama3.2", "url": "http://localhost:11434/v1/" }
}
```
