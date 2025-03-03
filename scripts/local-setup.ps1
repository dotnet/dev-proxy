# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

$versionString = "v0.26.0-beta.1"
$version = $versionString.Substring(1)
$isBeta = $version.Contains("-beta")

# Remove old installer if any
Get-Item ../bld/*installer* -ErrorAction SilentlyContinue | Remove-Item

if (-not (Test-Path ../bld)) {
    Write-Error "Build directory not found. Run local-build.ps1 first."
    exit 1
}

if ($isBeta) {
    # Rename executable for beta
    Rename-Item -Path ../bld/devproxy.exe -NewName devproxy-beta.exe
    # Set newVersionNotification for beta
    $content = Get-Content ../bld/devproxyrc.json
    $content -replace '"newVersionNotification": "stable"', '"newVersionNotification": "beta"' | Set-Content ../bld/devproxyrc.json
}

# Set icon filename
$icon = $isBeta ? "icon-beta.ico" : "icon.ico"

# Add installer icon
Copy-Item "../media/$icon" "../bld/$icon"

# Set installer filename
$installer = $isBeta ? "install-beta.iss" : "install.iss"

# Copy installer file
Copy-Item "../$installer" "../bld/$installer"

# Set version in installer script
if ($isBeta) {
    (Get-Content "../bld/$installer") -replace "#define MyAppVersion .*", "#define MyAppVersion `"$version`"" | Set-Content "../bld/$installer"
}


ISCC.exe "../bld/$installer" /F"dev-proxy-installer-win-x64-$versionString"