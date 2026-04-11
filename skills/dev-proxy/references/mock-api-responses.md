# Mock API Responses

Dev Proxy provides multiple plugins for mocking API responses — from simple predefined responses to full CRUD APIs with in-memory data stores, STDIO-based MCP server mocking, and authentication simulation.

## Choosing the Right Plugin

| Scenario | Plugin | When to use |
|----------|--------|-------------|
| Return predefined HTTP responses | `MockResponsePlugin` | Static mocks for any API |
| Full CRUD with data persistence | `CrudApiPlugin` | Frontend dev before backend exists |
| Microsoft Graph responses | `GraphMockResponsePlugin` | Graph-specific mocking with batch support |
| MCP server / STDIO responses | `MockStdioResponsePlugin` | Mock AI agent tools communicating via stdin/stdout |
| Simulate auth requirements | `AuthPlugin` | Test API key or OAuth2 flows |

## MockResponsePlugin

The most common mocking plugin. Returns predefined responses matched by URL, HTTP method, body content, and request count.

### Config

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "MockResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "mocksPlugin"
    }
  ],
  "urlsToWatch": ["https://api.contoso.com/*"],
  "mocksPlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/mockresponseplugin.schema.json",
    "mocksFile": "mocks.json",
    "blockUnmockedRequests": false
  }
}
```

Set `blockUnmockedRequests: true` to return 502 for any request without a matching mock.

CLI overrides: `--no-mocks` to disable, `--mocks-file <path>` to change mock file.

### Mock File Structure

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/mockresponseplugin.mocksfile.schema.json",
  "mocks": [
    {
      "request": {
        "url": "https://api.contoso.com/users",
        "method": "GET"
      },
      "response": {
        "statusCode": 200,
        "body": { "users": [] },
        "headers": [
          { "name": "content-type", "value": "application/json" }
        ]
      }
    }
  ]
}
```

### Matching Rules

- **URL wildcards**: `*` in URLs converts to `.*` regex. `https://api.contoso.com/users/*` matches any sub-path.
- **Ordering**: First matching mock wins. Place mocks with the longest (most specific) URLs first so a generic pattern like `/{id}` doesn't override a more specific one like `/category/{name}`.
- **Nth first**: Mocks with the `nth` property are more specific — define them before mocks without `nth` for the same URL.
- **Method**: Defaults to `GET` if omitted.
- **Body matching**: Use `bodyFragment` to match requests containing a specific string (non-GET only).
- **Nth request**: Use `nth: 2` to respond only on the 2nd call — useful for polling/long-running operations.

### Dynamic Responses

Mirror request data in responses using `@request.body.*` placeholders:

```json
{
  "request": { "url": "https://api.contoso.com/users", "method": "POST" },
  "response": {
    "statusCode": 201,
    "body": {
      "id": "abc-123",
      "name": "@request.body.name",
      "email": "@request.body.email"
    }
  }
}
```

Supports nested objects and arrays. `@request.body.tags` returns the full array. `@request.body.user.name` accesses nested properties.

### Binary Responses

Return files from disk using `@filename` syntax in the body:

```json
{
  "response": {
    "body": "@photo.jpg",
    "headers": [{ "name": "content-type", "value": "image/jpeg" }]
  }
}
```

File path is relative to the mocks file location.

### Polling / Long-Running Operations

Simulate a status endpoint that returns "inprogress" first, then "completed" on the second call:

```json
{
  "mocks": [
    {
      "request": {
        "url": "https://api.contoso.com/operations/op-123",
        "method": "GET",
        "nth": 2
      },
      "response": {
        "statusCode": 200,
        "body": { "id": "op-123", "status": "completed", "result": { "url": "https://api.contoso.com/items/1" } }
      }
    },
    {
      "request": {
        "url": "https://api.contoso.com/operations/op-123",
        "method": "GET"
      },
      "response": {
        "statusCode": 200,
        "body": { "id": "op-123", "status": "inprogress" },
        "headers": [{ "name": "retry-after", "value": "5" }]
      }
    }
  ]
}
```

Place the `nth` mock BEFORE the default mock — first match wins.

### Body Fragment Matching

Match POST requests containing a specific body fragment:

```json
{
  "request": {
    "url": "https://login.microsoftonline.com/*/oauth2/v2.0/token",
    "method": "POST",
    "bodyFragment": "scope=https%3A%2F%2Fapi.contoso.com%2FDocuments.Read"
  },
  "response": {
    "headers": [{ "name": "Content-Type", "value": "application/json; charset=utf-8" }],
    "body": {
      "token_type": "Bearer",
      "expires_in": 3599,
      "access_token": "eyJ0eXAiOiJKV1QiLCJhbGciOiJSU..."
    }
  }
}
```

## CrudApiPlugin

Creates a fully functional CRUD API backed by an in-memory JSON data store. Supports URL parameters and JSONPath queries.

### Config

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "CrudApiPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "customersApi"
    }
  ],
  "customersApi": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/crudapiplugin.schema.json",
    "apiFile": "customers-api.json"
  }
}
```

### API Definition File

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/crudapiplugin.apifile.schema.json",
  "baseUrl": "https://api.contoso.com/v1/customers",
  "dataFile": "customers-data.json",
  "actions": [
    { "action": "getAll" },
    { "action": "getOne", "url": "/{customer-id}", "query": "$.[?(@.id == {customer-id})]" },
    { "action": "create" },
    { "action": "merge", "url": "/{customer-id}", "query": "$.[?(@.id == {customer-id})]" },
    { "action": "delete", "url": "/{customer-id}", "query": "$.[?(@.id == {customer-id})]" }
  ]
}
```

### Action Types

| Action | Default Method | Description |
|--------|---------------|-------------|
| `getAll` | GET | Return all items |
| `getOne` | GET | Return single item (fails if query matches multiple) |
| `getMany` | GET | Return multiple items |
| `create` | POST | Add item (no shape validation) |
| `merge` | PATCH | Merge with existing item |
| `update` | PUT | Replace existing item |
| `delete` | DELETE | Remove item |

### Key Rules

- URL parameters use `{curly-braces}` and are substituted in both URL and query.
- JSONPath uses Newtonsoft implementation — **single quotes only** in expressions.
- The data file must be a JSON array (can be empty: `[]`).
- Data persists in memory during the Dev Proxy session.

### Entra Auth for CRUD APIs

Set `"auth": "entra"` in the API file:

```json
{
  "baseUrl": "https://api.contoso.com/v1/customers",
  "dataFile": "customers-data.json",
  "auth": "entra",
  "entraAuthConfig": {
    "audience": "https://api.contoso.com",
    "issuer": "https://login.microsoftonline.com/contoso.com",
    "scopes": ["api://contoso.com/user_impersonation"]
  },
  "actions": [
    { "action": "getAll" },
    { "action": "create" }
  ]
}
```

Per-action scopes are supported by adding `auth` and `entraAuthConfig` to individual actions.

| Property | Required | Default | Description |
|----------|----------|---------|-------------|
| `audience` | No | — | Valid audience for the token |
| `issuer` | No | — | Valid token issuer |
| `scopes` | No | — | Valid scopes (any match passes) |
| `roles` | No | — | Valid roles (any match passes) |
| `validateLifetime` | No | `false` | Validate token expiration |
| `validateSigningKey` | No | `false` | Validate token signature |

## MockStdioResponsePlugin

Mocks STDIO communication for MCP servers and STDIO-based apps. Use with `devproxy stdio <command>`.

### Config

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/rc.schema.json",
  "plugins": [
    {
      "name": "MockStdioResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "mockStdioResponsePlugin"
    }
  ],
  "mockStdioResponsePlugin": {
    "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/mockstdioresponseplugin.schema.json",
    "mocksFile": "stdio-mocks.json",
    "blockUnmockedRequests": false
  }
}
```

### STDIO Mock File

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/dev-proxy/main/schemas/v2.1.0/mockstdioresponseplugin.schema.json",
  "mocks": [
    {
      "request": { "bodyFragment": "initialize" },
      "response": {
        "stdout": "{\"jsonrpc\":\"2.0\",\"id\":@stdin.body.id,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"Mock MCP\",\"version\":\"1.0.0\"}}}\n"
      }
    },
    {
      "request": { "bodyFragment": "tools/list" },
      "response": {
        "stdout": "{\"jsonrpc\":\"2.0\",\"id\":@stdin.body.id,\"result\":{\"tools\":[]}}\n"
      }
    },
    {
      "request": { "bodyFragment": "tools/call" },
      "response": {
        "stdout": "{\"jsonrpc\":\"2.0\",\"id\":@stdin.body.id,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Result\"}]}}\n"
      }
    }
  ]
}
```

- `bodyFragment` (required): string to match in stdin.
- `stdout` / `stderr`: response content. Use `@filename` to load from file.
- `@stdin.body.*` placeholders: `@stdin.body.id`, `@stdin.body.method`, `@stdin.body.params.name`.
- `nth`: respond only on nth occurrence.
- `blockUnmockedRequests: true`: consume unmatched stdin without forwarding.

Run: `devproxy stdio npx -y @modelcontextprotocol/server-filesystem`

## AuthPlugin

Simulates API authentication. Place **before** `MockResponsePlugin` or `CrudApiPlugin` in the plugin array.

### API Key Auth

```json
{
  "plugins": [
    {
      "name": "AuthPlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "auth"
    },
    {
      "name": "MockResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "mocks"
    }
  ],
  "auth": {
    "type": "apiKey",
    "apiKey": {
      "parameters": [
        { "in": "header", "name": "x-api-key" },
        { "in": "query", "name": "code" }
      ],
      "allowedKeys": ["my-secret-key"]
    }
  }
}
```

Parameter locations: `header`, `query`, `cookie`. Multiple parameters checked — any match passes.

### OAuth2 Auth

```json
{
  "auth": {
    "type": "oauth2",
    "oauth2": {
      "metadataUrl": "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration",
      "allowedApplications": ["00000000-0000-0000-0000-000000000000"],
      "allowedAudiences": ["00000000-0000-0000-0000-000000000000"],
      "issuer": "https://login.microsoftonline.com/tenant-id/v2.0",
      "scopes": ["Posts.Read"],
      "roles": ["admin"],
      "validateLifetime": true,
      "validateSigningKey": true
    }
  }
}
```

Leave properties empty to skip validation for that claim.

## GraphMockResponsePlugin

Same config as `MockResponsePlugin` but adds Microsoft Graph batch request support. Batch requests (POST to `/$batch`) are decomposed and each sub-request is matched individually against mocks.

```json
{
  "plugins": [
    {
      "name": "GraphMockResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "graphMocks"
    }
  ],
  "urlsToWatch": ["https://graph.microsoft.com/v1.0/*", "https://graph.microsoft.com/beta/*"],
  "graphMocks": { "mocksFile": "graph-mocks.json" }
}
```

## Combining Mocking with Other Plugins

### Mocks + Latency

```json
{
  "plugins": [
    { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latency" },
    { "name": "MockResponsePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "mocks" }
  ],
  "latency": { "minMs": 200, "maxMs": 2000 }
}
```

### Mocks + Auth

Place `AuthPlugin` before `MockResponsePlugin`:

```json
{
  "plugins": [
    { "name": "AuthPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "auth" },
    { "name": "MockResponsePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "mocks" }
  ]
}
```

### STDIO Mocks + Latency + DevTools

```json
{
  "plugins": [
    { "name": "LatencyPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "latency" },
    { "name": "DevToolsPlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll" },
    { "name": "MockStdioResponsePlugin", "enabled": true, "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll", "configSection": "stdioMocks" }
  ],
  "latency": { "minMs": 100, "maxMs": 500 },
  "stdioMocks": { "mocksFile": "stdio-mocks.json" }
}
```

Run: `devproxy stdio npx -y @modelcontextprotocol/server-filesystem`
