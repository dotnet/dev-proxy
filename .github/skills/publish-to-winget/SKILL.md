---
name: publish-to-winget
description: >
  This skill should be used when the user asks to "publish to winget",
  "update winget", "submit to winget", "winget release", "create winget PR",
  or mentions publishing Dev Proxy to the Windows Package Manager.
  Handles creating manifest files, computing installer hashes, and submitting
  a pull request to the winget-pkgs repository.
---

# Publish Dev Proxy to Winget

Create winget manifest files and submit a PR to `microsoft/winget-pkgs` — entirely via the GitHub API. No local clone of winget-pkgs required.

## Prerequisites

Before starting, verify ALL of the following:

1. **`gh` CLI** is installed and authenticated (`gh auth status`)
2. **`curl`** is available
3. **`shasum`** (macOS) or **`sha256sum`** (Linux) is available for computing SHA256 hashes
4. **`awk`** is available
5. **The installer** `.exe` is already published at the GitHub Releases URL for the target version

If any prerequisite fails, stop and tell the user what's missing.

## Input

Ask the user for the **version string** if not provided. Format: `X.Y.Z` or `X.Y.Z-beta.N`.

Validate with pattern: `^(\d+)\.(\d+)\.(\d+)(-beta\.\d+)?$`

## Constants

```
UPSTREAM_REPO = microsoft/winget-pkgs
GITHUB_RELEASE_URL = https://github.com/dotnet/dev-proxy/releases/download/v
MANIFEST_SCHEMA_URL = https://aka.ms/winget-manifest
MANIFEST_VERSION = 1.12.0
```

> **Keeping the schema version current:** The `MANIFEST_VERSION` must match the
> latest schema version used in [winget-pkgs](https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema).
> Check before publishing — if a newer schema exists, update
> `MANIFEST_VERSION` in this file.

## Process

### Step 1: Determine Release Type

Parse the version string to determine if this is a beta release:

| Condition | `releaseFolder` | `packageIdentifier` | `packageName` |
|-----------|-----------------|---------------------|---------------|
| Version contains `beta` | `DevProxy/Beta` | `DevProxy.DevProxy.Beta` | `Dev Proxy Beta` |
| Otherwise | `DevProxy` | `DevProxy.DevProxy` | `Dev Proxy` |

### Step 2: Ensure Fork Exists

```bash
gh repo fork microsoft/winget-pkgs --clone=false
```

Determine the fork name (the authenticated user's fork):

```bash
FORK_REPO=$(gh api user --jq '.login')/winget-pkgs
```

### Step 3: Download Installer and Compute SHA256

Download the installer to a temporary file, validate the download succeeded, then compute the SHA256 hash:

```bash
INSTALLER_URL="${GITHUB_RELEASE_URL}<version>/dev-proxy-installer-win-x64-v<version>.exe"
INSTALLER_FILE=$(mktemp)
curl -fSL -o "${INSTALLER_FILE}" "${INSTALLER_URL}"
```

If `curl` exits with a non-zero status (e.g., HTTP 404), stop and tell the user the installer is not available at that URL.

Compute the hash from the downloaded file:

```bash
SHA256=$(shasum -a 256 "${INSTALLER_FILE}" | awk '{print $1}')
rm -f "${INSTALLER_FILE}"
```

On Linux, use `sha256sum` instead of `shasum -a 256`.

If the hash equals `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (SHA256 of an empty file), treat this as a failure.

### Step 4: Get Upstream master HEAD SHA

```bash
MASTER_SHA=$(gh api repos/microsoft/winget-pkgs/git/ref/heads/master --jq '.object.sha')
```

Also fetch the tree SHA from the master commit (needed for `base_tree` in Step 6b):

```bash
MASTER_TREE_SHA=$(gh api repos/microsoft/winget-pkgs/git/commits/${MASTER_SHA} --jq '.tree.sha')
```

### Step 5: Create Branch on Fork

Branch name format: `microsoft-devproxy-X-Y-Z` (dots replaced with dashes).

```bash
gh api repos/${FORK_REPO}/git/refs -f ref="refs/heads/<branch-name>" -f sha="${MASTER_SHA}"
```

If the branch already exists (422 error), update it instead:

```bash
gh api repos/${FORK_REPO}/git/refs/heads/<branch-name> -X PATCH -f sha="${MASTER_SHA}" -f force=true
```

### Step 6: Create Manifest Files via Git Data API

Build the manifest file content, then commit all three files in a single commit using the Git Data API.

The manifest path prefix is: `manifests/d/DevProxy/<releaseFolder>/<version>/`

**6a: Create blobs** for each file:

```bash
BLOB_SHA=$(gh api repos/${FORK_REPO}/git/blobs -f content="<file-content>" -f encoding="utf-8" --jq '.sha')
```

Create one blob per manifest file (3 total).

**6b: Create tree** with all three files:

```bash
gh api repos/${FORK_REPO}/git/trees \
  -f "base_tree=${MASTER_TREE_SHA}" \
  -f "tree[][path]=manifests/d/DevProxy/<releaseFolder>/<version>/<packageIdentifier>.installer.yaml" \
  -f "tree[][mode]=100644" \
  -f "tree[][type]=blob" \
  -f "tree[][sha]=<installer-blob-sha>" \
  -f "tree[][path]=manifests/d/DevProxy/<releaseFolder>/<version>/<packageIdentifier>.locale.en-US.yaml" \
  -f "tree[][mode]=100644" \
  -f "tree[][type]=blob" \
  -f "tree[][sha]=<locale-blob-sha>" \
  -f "tree[][path]=manifests/d/DevProxy/<releaseFolder>/<version>/<packageIdentifier>.yaml" \
  -f "tree[][mode]=100644" \
  -f "tree[][type]=blob" \
  -f "tree[][sha]=<version-blob-sha>"
```

**6c: Create commit**:

```bash
COMMIT_SHA=$(gh api repos/${FORK_REPO}/git/commits \
  -f message="New version: <packageIdentifier> version <version>" \
  -f "tree=<tree-sha>" \
  -f "parents[]=${MASTER_SHA}" \
  --jq '.sha')
```

**6d: Update branch ref**:

```bash
gh api repos/${FORK_REPO}/git/refs/heads/<branch-name> -X PATCH -f sha="${COMMIT_SHA}"
```

### Manifest File Content

Use the exact content below, substituting variables.

**File: `<packageIdentifier>.installer.yaml`**

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.<MANIFEST_VERSION>.schema.json

PackageIdentifier: <packageIdentifier>
PackageVersion: <version>
InstallerType: inno
Installers:
 - InstallerUrl: https://github.com/dotnet/dev-proxy/releases/download/v<version>/dev-proxy-installer-win-x64-v<version>.exe
   Architecture: x64
   InstallerSha256: <sha256-hash>
ManifestType: installer
ManifestVersion: <MANIFEST_VERSION>
```

**File: `<packageIdentifier>.locale.en-US.yaml`**

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.<MANIFEST_VERSION>.schema.json

PackageIdentifier: <packageIdentifier>
PackageVersion: <version>
PackageLocale: en-US
Publisher: .NET Foundation
PackageName: <packageName>
License: MIT License
ShortDescription: <shortDescription>
ManifestType: defaultLocale
ManifestVersion: <MANIFEST_VERSION>
```

> **ShortDescription rules:** Must be 3–256 characters. Must NOT be just
> "package name installer" or "package name setup". Use a real description.
>
> | Release type | `shortDescription` |
> |-------------|---------------------|
> | Stable | `Dev Proxy is a command line tool for simulating APIs for testing apps` |
> | Beta | `Dev Proxy Beta is a preview build of the command line tool for simulating APIs for testing apps` |

**File: `<packageIdentifier>.yaml`**

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.<MANIFEST_VERSION>.schema.json

PackageIdentifier: <packageIdentifier>
PackageVersion: <version>
DefaultLocale: en-US
ManifestType: version
ManifestVersion: <MANIFEST_VERSION>
```

### Step 7: Create Pull Request

```bash
FORK_OWNER=$(gh api user --jq '.login')
gh pr create \
  --repo microsoft/winget-pkgs \
  --head "${FORK_OWNER}:<branch-name>" \
  --base master \
  --title "New version: <packageIdentifier> version <version>" \
  --body "New version: <packageIdentifier> version <version>"
```

Show the user the PR URL when done.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| `gh` not found | Install with `brew install gh` then `gh auth login` |
| `gh auth` not logged in | Run `gh auth login` |
| Installer 404 | Verify the release exists at `https://github.com/dotnet/dev-proxy/releases/tag/v<version>` |
| Empty SHA256 hash | The curl download failed — check the URL and your network |
| Branch already exists (422) | The script handles this by force-updating the existing branch |
| Fork already exists | `gh repo fork` is idempotent — safe to re-run |
