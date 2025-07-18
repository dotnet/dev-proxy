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
Write-Host "1. Create the 'devproxy' directory in the current working folder"
Write-Host "2. Download the latest Dev Proxy release"
Write-Host "3. Unzip the release in the devproxy directory"
Write-Host "4. Configure devproxy and its files as executable (Linux and macOS only)"
Write-Host "5. Add the devproxy directory to your PATH environment variable in `$PROFILE.CurrentUserAllHosts"
Write-Host ""
Write-Host "Continue (Y/n)? " -NoNewline
$key = [Console]::ReadKey($true)
$response = $key.KeyChar

if ($response -notin @('Y', 'y', "`r")) {
    Write-Host "`nExiting"
    exit 1
}

Write-Host "`n"

New-Item -ItemType Directory -Force -Path .\devproxy -ErrorAction Stop | Out-Null
Set-Location .\devproxy | Out-Null

# Get the full path of the current directory
$full_path = Resolve-Path .

if (-not $env:DEV_PROXY_VERSION) {
    # Get the latest Dev Proxy version
    Write-Host "Getting latest Dev Proxy version..."
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/dotnet/dev-proxy/releases/latest" -ErrorAction Stop
    $version = $response.tag_name
    Write-Host "Latest version is $version"
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
chmod +x ./devproxy ./libe_sqlite3.so

if (!(Test-Path $PROFILE.CurrentUserAllHosts)) {
    Write-Host "Creating `$PROFILE.CurrentUserAllHosts..."
    New-Item -ItemType File -Force -Path $PROFILE.CurrentUserAllHosts | Out-Null
}

if (!(Select-String -Path $PROFILE.CurrentUserAllHosts -Pattern "devproxy")) {
    Write-Host "Adding devproxy to `$PROFILE.CurrentUserAllHosts..."
    Add-Content -Path $PROFILE.CurrentUserAllHosts -Value "$([Environment]::NewLine)`$env:PATH += `"$([IO.Path]::PathSeparator)$full_path`""
}

Write-Host "Dev Proxy $version installed!"
Write-Host
Write-Host "To get started, run:"
Write-Host "    . `$PROFILE.CurrentUserAllHosts"
Write-Host "    devproxy --help"