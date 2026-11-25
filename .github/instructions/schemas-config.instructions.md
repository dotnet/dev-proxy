---
applyTo: "schemas/**,DevProxy/config/**,DevProxy/config-templates/**,DevProxy/devproxyrc.*,DevProxy/devproxy-errors.*"
excludeAgent: "cloud-agent"
---

# Code review: schemas and configuration

## Check

- **Versioning.** Schemas live in `schemas/v<version>/`. Edits should target the folder matching the version in `DevProxy/DevProxy.csproj`; older version folders are published and should not change. `$schema` URLs in shipped config files must point to the current version.
- **Schema matches runtime.** Property names, types, enums, `required`, and `default` values must match the C# configuration classes in `DevProxy.Plugins` / `DevProxy`. A default in the schema that differs from the code default is a bug.
- **Valid JSON Schema.** Correct draft keywords, `additionalProperties` where the runtime rejects unknown keys, `pattern`/`format` that actually match the values the runtime accepts.
- **Sample configs work.** Files in `DevProxy/config/` should load without errors: plugin names and `pluginPath` exist, referenced files exist relative to the config file, plugin order is sensible (latency first, simulators before reporters, reporters last), URLs in `urlsToWatch` are ordered most-specific first.
- **JSON and YAML parity.** When both `.json` and `.yaml` variants exist, they should express the same configuration.

## Don't flag

- Files under `DevProxy/config-templates/` that are intentionally incomplete (empty list entries, placeholders) and not valid on their own.
- Security concerns about file paths or URLs in config. Config is written by the local user.
