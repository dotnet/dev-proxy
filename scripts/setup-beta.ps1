# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

if (-not $IsLinux)
{
    Write-Error "Unsupported OS. This script is for installing Dev Proxy on Linux. To install Dev Proxy on macOS or Windows use their installers. For more information, visit https://aka.ms/devproxy/start."
    exit 1
}

Write-Host ""
Write-Host "This script installs Dev Proxy on your machine. It runs the following steps:"
Write-Host ""
Write-Host "1. Create the 'devproxy-beta' directory in the current working folder"
Write-Host "2. Download the latest beta Dev Proxy release"
Write-Host "3. Unzip the release in the devproxy-beta directory"
Write-Host "4. Configure Dev Proxy and its files as executable (Linux and macOS only)"
Write-Host "5. Configure new version notifications for the beta channel"
Write-Host "6. Add the devproxy-beta directory to your PATH environment variable in `$PROFILE.CurrentUserAllHosts"
Write-Host ""
Write-Host "Continue (y/n)? " -NoNewline
$response = [System.Console]::ReadKey().KeyChar

if ($response -notin @('y', 'Y')) {
    Write-Host "`nExiting"
    exit 1
}

Write-Host "`n"

New-Item -ItemType Directory -Force -Path .\devproxy-beta -ErrorAction Stop | Out-Null
Set-Location .\devproxy-beta | Out-Null

# Get the full path of the current directory
$full_path = Resolve-Path .

if (-not $env:DEV_PROXY_VERSION) {
    # Get the latest beta Dev Proxy version
    Write-Host "Getting latest beta Dev Proxy version..."
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/dotnet/dev-proxy/releases?per_page=2" -ErrorAction Stop
    $version = $response | Where-Object { $_.tag_name -like "*-beta*" } | Select-Object -First 1 | Select-Object -ExpandProperty tag_name
    Write-Host "Latest beta version is $version"
} else {
    $version = $env:DEV_PROXY_VERSION
}

# Download Dev Proxy
Write-Host "Downloading Dev Proxy $version..."
$base_url = "https://github.com/dotnet/dev-proxy/releases/download/$version/dev-proxy"

if ($arch -eq "X64") {
    $url = "$base_url-linux-x64-$version.zip"
} elseif ($arch -eq "Arm64") {
    $url = "$base_url-linux-arm64-$version.zip"
} else {
    Write-Host "Unsupported architecture $arch. Aborting"
    exit 1
}

Invoke-WebRequest -Uri $url -OutFile devproxy.zip -ErrorAction Stop
Add-Type -AssemblyName System.IO.Compression.FileSystem
Expand-Archive -Path devproxy.zip -DestinationPath . -Force -ErrorAction Stop
Remove-Item devproxy.zip

Write-Host "Configuring devproxy and its files as executable..."
chmod +x ./devproxy-beta ./libe_sqlite3.so

Write-Host "Configuring new version notifications for the beta channel..."
(Get-Content -Path devproxyrc.json) -replace '"newVersionNotification": "stable"', '"newVersionNotification": "beta"' | Set-Content -Path devproxyrc.json

if (!(Test-Path $PROFILE.CurrentUserAllHosts)) {
    Write-Host "Creating `$PROFILE.CurrentUserAllHosts..."
    New-Item -ItemType File -Force -Path $PROFILE.CurrentUserAllHosts | Out-Null
}

if (!(Select-String -Path $PROFILE.CurrentUserAllHosts -Pattern "devproxy")) {
    Write-Host "Adding Dev Proxy to `$PROFILE.CurrentUserAllHosts..."
    Add-Content -Path $PROFILE.CurrentUserAllHosts -Value "$([Environment]::NewLine)`$env:PATH += `"$([IO.Path]::PathSeparator)$full_path`""
}

Write-Host "Dev Proxy $version installed!"
Write-Host
Write-Host "To get started, run:"
Write-Host "    . `$PROFILE.CurrentUserAllHosts"
Write-Host "    devproxy-beta --help"