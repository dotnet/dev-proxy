#!/bin/bash
# upgrade-version.sh - Upgrade Dev Proxy version
# Usage: ./upgrade-version.sh <new-version> [workspace-root]
#
# Example: ./upgrade-version.sh 2.1.0 /path/to/dev-proxy

set -e

NEW_VERSION="${1}"
WORKSPACE_ROOT="${2:-.}"

if [[ -z "$NEW_VERSION" ]]; then
    echo "Usage: $0 <new-version> [workspace-root]"
    echo "Example: $0 2.1.0 /path/to/dev-proxy"
    exit 2
fi

# Validate workspace
if [[ ! -f "$WORKSPACE_ROOT/DevProxy.sln" ]]; then
    echo "Error: DevProxy.sln not found in $WORKSPACE_ROOT"
    exit 1
fi

# Extract current version from DevProxy.csproj
CURRENT_VERSION=$(sed -n 's/.*<Version>\([^<][^<]*\)<\/Version>.*/\1/p' "$WORKSPACE_ROOT/DevProxy/DevProxy.csproj" | head -1)

if [[ -z "$CURRENT_VERSION" ]]; then
    echo "Error: Could not determine current version"
    exit 1
fi

echo "Upgrading Dev Proxy from $CURRENT_VERSION to $NEW_VERSION"
echo "Workspace: $WORKSPACE_ROOT"
echo ""

# Step 1: Copy schema folder
echo "Step 1: Copying schema folder..."
if [[ -d "$WORKSPACE_ROOT/schemas/v$CURRENT_VERSION" ]]; then
    cp -r "$WORKSPACE_ROOT/schemas/v$CURRENT_VERSION" "$WORKSPACE_ROOT/schemas/v$NEW_VERSION"
    echo "  Created: schemas/v$NEW_VERSION"
else
    echo "  Warning: schemas/v$CURRENT_VERSION not found, skipping schema copy"
fi

# Step 2: Update csproj files (Category A)
echo ""
echo "Step 2: Updating csproj Version tags..."
for csproj in DevProxy/DevProxy.csproj DevProxy.Abstractions/DevProxy.Abstractions.csproj DevProxy.Plugins/DevProxy.Plugins.csproj; do
    if [[ -f "$WORKSPACE_ROOT/$csproj" ]]; then
        sed -i '' "s|<Version>$CURRENT_VERSION</Version>|<Version>$NEW_VERSION</Version>|g" "$WORKSPACE_ROOT/$csproj"
        echo "  Updated: $csproj"
    fi
done

# Step 3: Update schema URLs in JSON and CS files (Category B)
echo ""
echo "Step 3: Updating schema URLs..."
# JSON files
for json in DevProxy/devproxyrc.json DevProxy/devproxy-errors.json DevProxy/config/*.json; do
    if [[ -f "$WORKSPACE_ROOT/$json" ]]; then
        sed -i '' "s|schemas/v$CURRENT_VERSION/|schemas/v$NEW_VERSION/|g" "$WORKSPACE_ROOT/$json"
        echo "  Updated: $json"
    fi
done

# CS files with hardcoded schema URLs
MOCK_PLUGIN="$WORKSPACE_ROOT/DevProxy.Plugins/Mocking/MockResponsePlugin.cs"
if [[ -f "$MOCK_PLUGIN" ]]; then
    sed -i '' "s|schemas/v$CURRENT_VERSION/|schemas/v$NEW_VERSION/|g" "$MOCK_PLUGIN"
    echo "  Updated: DevProxy.Plugins/Mocking/MockResponsePlugin.cs"
fi

# Step 4: Update installer files (Category C)
echo ""
echo "Step 4: Updating installer files..."
# Stable installer
if [[ -f "$WORKSPACE_ROOT/install.iss" ]]; then
    sed -i '' "s|dev-proxy-installer-win-x64-$CURRENT_VERSION\"|dev-proxy-installer-win-x64-$NEW_VERSION\"|g" "$WORKSPACE_ROOT/install.iss"
    sed -i '' "s|MyAppVersion \"$CURRENT_VERSION\"|MyAppVersion \"$NEW_VERSION\"|g" "$WORKSPACE_ROOT/install.iss"
    echo "  Updated: install.iss"
fi

# Beta installer (handle beta suffix separately if needed)
if [[ -f "$WORKSPACE_ROOT/install-beta.iss" ]]; then
    # Extract current beta version
    CURRENT_BETA=$(sed -n 's/.*MyAppVersion "\([^"]*\)".*/\1/p' "$WORKSPACE_ROOT/install-beta.iss" | head -1)
    if [[ -n "$CURRENT_BETA" ]]; then
        # Determine new beta version - if NEW_VERSION doesn't have beta suffix, add -beta.1
        if [[ "$NEW_VERSION" == *"-beta"* ]]; then
            NEW_BETA="$NEW_VERSION"
        else
            NEW_BETA="${NEW_VERSION}-beta.1"
        fi
        sed -i '' "s|dev-proxy-installer-win-x64-$CURRENT_BETA\"|dev-proxy-installer-win-x64-$NEW_BETA\"|g" "$WORKSPACE_ROOT/install-beta.iss"
        sed -i '' "s|MyAppVersion \"$CURRENT_BETA\"|MyAppVersion \"$NEW_BETA\"|g" "$WORKSPACE_ROOT/install-beta.iss"
        echo "  Updated: install-beta.iss (to $NEW_BETA)"
    fi
fi

# Step 5: Update PowerShell scripts (Category D)
echo ""
echo "Step 5: Updating PowerShell scripts..."
for ps1 in scripts/local-setup.ps1 scripts/version.ps1; do
    if [[ -f "$WORKSPACE_ROOT/$ps1" ]]; then
        # These use format like "v2.0.0-beta.1"
        CURRENT_PS_VERSION=$(sed -n 's/.*versionString = "\([^"]*\)".*/\1/p' "$WORKSPACE_ROOT/$ps1" | head -1)
        if [[ -n "$CURRENT_PS_VERSION" ]]; then
            # Determine new PS version
            if [[ "$NEW_VERSION" == *"-beta"* ]]; then
                NEW_PS_VERSION="v$NEW_VERSION"
            else
                NEW_PS_VERSION="v${NEW_VERSION}-beta.1"
            fi
            sed -i '' "s|$CURRENT_PS_VERSION|$NEW_PS_VERSION|g" "$WORKSPACE_ROOT/$ps1"
            echo "  Updated: $ps1 (to $NEW_PS_VERSION)"
        fi
    fi
done

# Step 6: Update Dockerfiles (Category E)
echo ""
echo "Step 6: Updating Dockerfiles..."
# Stable Dockerfiles
for dockerfile in Dockerfile Dockerfile_beta; do
    if [[ -f "$WORKSPACE_ROOT/$dockerfile" ]]; then
        sed -i '' "s|DEVPROXY_VERSION=$CURRENT_VERSION|DEVPROXY_VERSION=$NEW_VERSION|g" "$WORKSPACE_ROOT/$dockerfile"
        echo "  Updated: $dockerfile"
    fi
done

# Beta/local Dockerfile (uses beta suffix)
if [[ -f "$WORKSPACE_ROOT/scripts/Dockerfile_local" ]]; then
    # Extract current version from Dockerfile_local
    CURRENT_DOCKER_LOCAL=$(sed -n 's/.*DEVPROXY_VERSION=\([^ ]*\).*/\1/p' "$WORKSPACE_ROOT/scripts/Dockerfile_local" | head -1)
    if [[ -n "$CURRENT_DOCKER_LOCAL" ]]; then
        if [[ "$NEW_VERSION" == *"-beta"* ]]; then
            NEW_DOCKER_LOCAL="$NEW_VERSION"
        else
            NEW_DOCKER_LOCAL="${NEW_VERSION}-beta.1"
        fi
        sed -i '' "s|DEVPROXY_VERSION=$CURRENT_DOCKER_LOCAL|DEVPROXY_VERSION=$NEW_DOCKER_LOCAL|g" "$WORKSPACE_ROOT/scripts/Dockerfile_local"
        echo "  Updated: scripts/Dockerfile_local (to $NEW_DOCKER_LOCAL)"
    fi
fi

echo ""
echo "=== Upgrade Complete ==="
echo "From: $CURRENT_VERSION"
echo "To:   $NEW_VERSION"
echo ""
echo "Next steps:"
echo "1. Review changes: git diff"
echo "2. Build: dotnet build"
echo "3. Test: dotnet run --project DevProxy"
