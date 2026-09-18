---
name: upgrade-devproxy-version
description: >
  This skill should be used when the user asks to "upgrade Dev Proxy version",
  "bump Dev Proxy to a new version", "update version to X.Y.Z", "release new
  Dev Proxy version", or mentions version upgrades in the Dev Proxy repository.
  Handles copying schema folders and updating version strings across csproj,
  config, installer, and source files.
---

# Upgrade Dev Proxy Version

Bump Dev Proxy from current version to a new version across all project files.

## Prerequisites

Confirm the Dev Proxy workspace is open:
- Workspace root: contains `DevProxy.sln`
- Schema folder: `./schemas/v<current-version>/` exists
- Project files: `DevProxy/`, `DevProxy.Abstractions/`, `DevProxy.Plugins/` present

## Upgrade Process

### Step 1: Identify Current Version

Find current version from `DevProxy/DevProxy.csproj`:

```xml
<Version>X.Y.Z</Version>
```

### Step 2: Copy Schema Folder

Copy the schema folder to the new version:

```bash
cp -r ./schemas/v<current-version> ./schemas/v<new-version>
```

### Step 3: Update Version Strings

Update version in all relevant files. There are two categories:

**Category A: Assembly/Package Version** (format: `X.Y.Z`)
Update `<Version>X.Y.Z</Version>` in:
- `DevProxy/DevProxy.csproj`
- `DevProxy.Abstractions/DevProxy.Abstractions.csproj`
- `DevProxy.Plugins/DevProxy.Plugins.csproj`

**Category B: Schema URLs** (format: `vX.Y.Z` in URL paths)
Update `schemas/v<old>/` to `schemas/v<new>/` in:
- `DevProxy/devproxyrc.json` - `$schema` URLs
- `DevProxy/devproxy-errors.json` - `$schema` URL
- `DevProxy/config/m365.json` - all `$schema` URLs
- `DevProxy/config/m365-mocks.json` - `$schema` URL
- `DevProxy/config/microsoft-graph.json` - all `$schema` URLs
- `DevProxy/config/microsoft-graph-rate-limiting.json` - `$schema` URL
- `DevProxy/config/spo-csom-types.json` - `$schema` URL
- `DevProxy/config-templates/devproxyrc.json` - `$schema` URL
- `DevProxy/config-templates/devproxyrc.yaml` - `$schema` URL
- `DevProxy.Plugins/Mocking/MockResponsePlugin.cs` - hardcoded schema URL

**Category C: Installer Files** (format: `X.Y.Z` or `X.Y.Z-beta.N`)
Update version in:
- `install.iss`:
  - `#define MyAppSetupExeName "dev-proxy-installer-win-x64-X.Y.Z"`
  - `#define MyAppVersion "X.Y.Z"`
- `install-beta.iss`:
  - `#define MyAppSetupExeName "dev-proxy-installer-win-x64-X.Y.Z-beta.N"`
  - `#define MyAppVersion "X.Y.Z-beta.N"`

**Category D: Script Files** (format: `vX.Y.Z` or `vX.Y.Z-beta.N`)
Update version string in:
- `scripts/local-setup.ps1` - `$versionString = "vX.Y.Z-beta.N"`
- `scripts/version.ps1` - `$script:versionString = "vX.Y.Z-beta.N"`

**Category E: Dockerfiles** (format: `X.Y.Z` or `X.Y.Z-beta.N`)
Update `DEVPROXY_VERSION` ARG in:
- `Dockerfile` - `ARG DEVPROXY_VERSION=X.Y.Z`
- `Dockerfile_beta` - `ARG DEVPROXY_VERSION=X.Y.Z`
- `scripts/Dockerfile_local` - `ARG DEVPROXY_VERSION=X.Y.Z-beta.N`

## Version Formats

Different files use different version formats:

| Location | Format | Example |
|----------|--------|---------|
| csproj `<Version>` | `X.Y.Z` | `2.1.0` |
| Schema URL path | `vX.Y.Z` | `v2.1.0` |
| Installer stable | `X.Y.Z` | `2.1.0` |
| Installer beta | `X.Y.Z-beta.N` | `2.1.0-beta.1` |
| Script version | `vX.Y.Z-beta.N` | `v2.1.0-beta.1` |
| Dockerfile stable | `X.Y.Z` | `2.1.0` |
| Dockerfile beta | `X.Y.Z-beta.N` | `2.1.0-beta.1` |

## Execution Steps

1. **Ask** for the target version if not provided
2. **Detect** current version from `DevProxy/DevProxy.csproj`
3. **Copy** schema folder: `cp -r ./schemas/v{old} ./schemas/v{new}`
4. **Update** Category A files (csproj Version tags)
5. **Update** Category B files (schema URLs in JSON and CS files)
6. **Update** Category C files (installer definitions)
7. **Update** Category D files (PowerShell version strings)
8. **Update** Category E files (Dockerfiles)
9. **Report** summary of changes made

## Automation Script

Use the bundled script to automate the upgrade:

```bash
.github/skills/upgrade-devproxy-version/scripts/upgrade-version.sh <new-version> [workspace-root]
```

Example:
```bash
.github/skills/upgrade-devproxy-version/scripts/upgrade-version.sh 2.1.0 .
```

The script handles all categories automatically, including detecting the current version and applying appropriate beta suffixes where needed.

## Important Notes

- Schema folder copy creates a new folder; existing version folder is preserved
- Do NOT update `System.CommandLine` package version (coincidentally `2.0.0-beta5`)
- Do NOT update `.vscode/tasks.json` version (different context)
- Beta releases: installer-beta.iss and script files use `-beta.N` suffix
- Stable releases: install.iss uses plain `X.Y.Z`

## Search Patterns

To find all version references, use:

```bash
# Assembly versions
grep -r "X\.Y\.Z" --include="*.csproj"

# Schema URLs  
grep -r "schemas/vX\.Y\.Z" --include="*.json" --include="*.cs"

# Installer versions
grep -r "X\.Y\.Z" --include="*.iss"

# Script versions
grep -r "vX\.Y\.Z" --include="*.ps1"
```

## Example: Upgrade from 2.0.0 to 2.1.0

```bash
# Step 1: Copy schemas
cp -r ./schemas/v2.0.0 ./schemas/v2.1.0

# Step 2: Find and update all version strings
# (Use grep/sed or editor find-replace)
```

Files to update:
- 3 csproj files: `2.0.0` → `2.1.0`
- ~20 schema URLs: `v2.0.0` → `v2.1.0`
- 2 installer files: version defines
- 2 PowerShell scripts: version strings
- 3 Dockerfiles: DEVPROXY_VERSION ARG

## Verification

After upgrade, verify:

1. All three csproj files show new `<Version>`
2. Schema folder `./schemas/v{new}/` exists
3. All JSON config files reference new schema version
4. `dotnet build` succeeds
