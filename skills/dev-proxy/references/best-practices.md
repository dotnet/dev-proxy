# Best Practices for Configuring and Using Dev Proxy

## Configuration Files

- Dev Proxy configuration file is named `devproxyrc.json` or `devproxyrc.jsonc` (to include comments).
- Store all Dev Proxy files in the `.devproxy` folder in the workspace.
- Schema version must match the installed Dev Proxy version. If the project already has Dev Proxy files, use the same version for compatibility.
- Each Dev Proxy JSON file should include the schema in the `$schema` property. File contents should be valid according to that schema.
- In the configuration file, list `plugins` first, followed by `urlsToWatch`, then plugin config sections.

## Configuring URLs to Watch

- Put the most specific URLs first. Dev Proxy matches URLs in the order they are defined.
- To exclude a URL from being watched, prepend it with `!`.
- Use `*` as a wildcard in URLs to match multiple URLs that follow a pattern.
- Prefer specifying `urlsToWatch` in the configuration file over plugin-specific `urlsToWatch` properties. Use plugin-specific `urlsToWatch` only to override the global configuration for a specific plugin.
- Plugins inherit the global `urlsToWatch` configuration. No need to define `urlsToWatch` for each plugin unless overriding the global configuration.
- If a plugin-specific `urlsToWatch` is defined, it overrides the global configuration for that plugin only.
- If a plugin instance has no `urlsToWatch`, at least one global `urlsToWatch` must be defined.

## Plugins

- The order of plugins in the configuration file matters. Dev Proxy executes plugins in the order they are listed.
- Plugins that can simulate a response should be put last, right before reporters.
- When adding plugin config sections, include `$schema` property for validation.
- Multiple instances of the same plugin can be used to simulate different scenarios (e.g., latency for an LLM vs. a regular API). Use a clear name for each plugin's configuration section.
- Reporter plugins are always placed after other plugins.
- When simulating throttling, use `RetryAfterPlugin` to verify that the client backs off for the prescribed time. Put `RetryAfterPlugin` as the first plugin in the configuration.

## Mocking

- When defining mock responses or CrudApiPlugin actions, put entries with the longest (most specific) URLs first. Entries are matched in the order they're defined.
- Mocks with the `nth` property should be defined first, because they're considered more specific than mocks without that property.
- To return a dynamic Retry-After header value in mock responses, use `@dynamic` as the header's value.
- When simulating APIs and their responses, consider using `LatencyPlugin` to make API responses feel more realistic.
- If using `LatencyPlugin`, put it before other plugins in the configuration file so latency is simulated before mock responses are returned.

## File Paths

- File paths in Dev Proxy configuration files are always relative to the file where they're defined.

## Hot Reload

- Dev Proxy supports hot reload of configuration files (v2.1.0+). Modifying the configuration file while Dev Proxy is running automatically detects changes and restarts with the new configuration.
- Hot reload works for the main configuration file and plugin-specific configuration files (mock files, CRUD API data files, etc.).
- No manual restart needed after making configuration changes — save the file and changes take effect automatically.

## curl

- When providing `curl` commands, include `-ikx http://127.0.0.1:8000` so curl ignores SSL certificate errors and uses Dev Proxy, e.g., `curl -ikx http://127.0.0.1:8000 https://jsonplaceholder.typicode.com/posts/1`.
