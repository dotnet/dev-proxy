---
applyTo: "schemas/**"
---

# JSON schema versioning

- Each `schemas/vX.Y.Z` folder is the schema for that Dev Proxy release. The current version is `<Version>` in `DevProxy/DevProxy.csproj`.
- **New properties, plugins or options go only in the current version's folder.** Don't add them to older versions; the feature doesn't exist in those releases.
- **Fixes to an existing property** (wrong type, constraint, enum, description) go in every version folder since the property was introduced, so all supported versions validate correctly.
- Don't otherwise edit schemas of already-shipped versions.
- Keep the schema in sync with the plugin's configuration class (property names, types and allowed values).
- Creating a new version folder is part of the version bump; use the `upgrade-devproxy-version` skill.
