---
applyTo: "scripts/**,**/*.sh,**/*.ps1,**/*.iss,**/Dockerfile*,features/**,.github/workflows/**"
excludeAgent: "cloud-agent"
---

# Code review: scripts, installers and workflows

## Shell and PowerShell scripts

- Quote variables and paths; assume paths with spaces (macOS `Application Support`, Windows `Program Files`, user names with spaces).
- Scripts must work on the shells and OS versions they target: macOS ships Bash 3.2 and BSD tools (`sed -i ''`, no GNU-only flags); PowerShell scripts should work in PowerShell 7 and, where used by installers, Windows PowerShell 5.1.
- Fail fast (`set -e`/`$ErrorActionPreference = 'Stop'`) and check exit codes of critical commands.
- Certificate trust/removal and system proxy toggling must be reversible and must not leave the system misconfigured on failure.
- Downloads in setup scripts must use HTTPS and pinned/verified release assets; architecture detection (x64/arm64) must be correct.

## Installers and containers

- Inno Setup (`install*.iss`) and Docker files: version strings, paths and PATH changes consistent with the release; uninstall reverses install.
- Dockerfiles: pinned base image tags, no secrets baked into layers, non-root user where feasible.

## GitHub workflows

- Pin third-party actions to a full commit SHA; keep `permissions:` minimal and explicit.
- Don't interpolate untrusted event data (`github.event.*.title`, `body`, `head_ref`) directly into `run:`; pass through `env:` instead.
- `pull_request_target` and `workflow_run` jobs must not check out or execute PR code with write tokens or secrets.
- Matrix and artifact names must stay consistent with release and packaging steps that consume them.

## Don't flag

- Scripts or workflow inputs that only take values from maintainers or the local user as injection risks.
