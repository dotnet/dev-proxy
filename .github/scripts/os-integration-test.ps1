#Requires -Version 7.4
<#
.SYNOPSIS
    Exercises Dev Proxy's real OS integration on a throwaway CI machine: registering as the
    system proxy, installing/trusting the root certificate, hot reload, and restoring the
    system proxy on stop and after a crash.

.DESCRIPTION
    Used by .github/workflows/os-integration.yml on hosted macOS and Windows runners.

    DO NOT RUN THIS ON YOUR OWN MACHINE. It changes system-wide proxy settings, trusts a
    root certificate machine-wide (Windows LocalMachine root store, macOS System keychain), and kills Dev Proxy
    processes. It refuses to run outside GitHub Actions.

.PARAMETER AppDir
    Folder containing the built devproxy executable (e.g. DevProxy/bin/Release/net10.0).

.PARAMETER BreakSystemProxy
    Deliberately break system proxy registration to verify that the workflow fails and
    reports an issue. macOS: replaces toggle-proxy.sh with a no-op. Windows: clears
    ProxyEnable right after Dev Proxy registers itself.

.PARAMETER StrictHotReload
    Fail (instead of warn) when the system proxy isn't registered after a hot reload.
    Enable once https://github.com/dotnet/dev-proxy/pull/2149 is merged.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AppDir,
    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'devproxy-os-integration'),
    [int] $Port = 8000,
    [int] $ApiPort = 8897,
    [switch] $BreakSystemProxy,
    [switch] $StrictHotReload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'This script changes system-wide settings and only runs on throwaway GitHub Actions runners.'
}
if (-not ($IsMacOS -or $IsWindows)) {
    throw 'OS integration is only implemented on macOS and Windows.'
}

# Make sure nothing but the OS settings can route the probe through a proxy.
foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy') {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

$AppDir = (Resolve-Path $AppDir).Path
$devProxy = Join-Path $AppDir ($IsWindows ? 'devproxy.exe' : 'devproxy')
if (-not (Test-Path $devProxy)) { throw "Dev Proxy executable not found at $devProxy" }

$expectedProxy = "127.0.0.1:$Port"
$mockUrl = 'https://devproxy-os-integration.example.com/hello'
$mockMarker = 'dev-proxy-os-integration-mock'

#region helpers

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "=== $message" -ForegroundColor Cyan
}

function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) {
        Write-Host "::error::$message"
        throw "Assertion failed: $message"
    }
    Write-Host "  PASS: $message" -ForegroundColor Green
}

function Wait-Until([scriptblock] $condition, [int] $timeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return [bool](& $condition)
}

function Invoke-DevProxy([string[]] $arguments, [switch] $Quiet) {
    Write-Host "> devproxy $($arguments -join ' ')"
    # Redirect to files rather than pipes: with --detach the background process inherits
    # the caller's output handles on Windows, so reading a pipe to EOF would never finish.
    $stdout = Join-Path $WorkDir 'devproxy-stdout.txt'
    $stderr = Join-Path $WorkDir 'devproxy-stderr.txt'
    $argumentLine = ($arguments | ForEach-Object { $_ -match '\s' ? "`"$_`"" : $_ }) -join ' '
    $process = Start-Process -FilePath $devProxy -ArgumentList $argumentLine -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(120000)) {
        $process.Kill()
        throw "devproxy $($arguments -join ' ') didn't exit within 2 minutes"
    }
    $output = @(Get-Content $stdout) + @(Get-Content $stderr)
    if (-not $Quiet) { $output | ForEach-Object { Write-Host "  $_" } }
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
}

function Start-DevProxy([bool] $asSystemProxy = $true, [bool] $installCert = $true) {
    $result = Invoke-DevProxy @(
        '--config-file', $script:configFile,
        '--detach', '--output', 'json',
        '--port', "$Port", '--api-port', "$ApiPort",
        '--as-system-proxy', "$asSystemProxy".ToLowerInvariant(),
        '--install-cert', "$installCert".ToLowerInvariant())
    Assert-True ($result.ExitCode -eq 0) "devproxy started (exit code $($result.ExitCode))"

    $info = $result.Output |
        Where-Object { $_.TrimStart().StartsWith('{') } |
        ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } |
        Where-Object { $_ -and $_.PSObject.Properties['data'] -and $_.data.PSObject.Properties['pid'] } |
        Select-Object -Last 1
    Assert-True ($null -ne $info) 'devproxy --detach reported its PID'
    return $info.data
}

function Stop-DevProxy {
    $result = Invoke-DevProxy @('stop')
    Assert-True ($result.ExitCode -eq 0) "devproxy stop succeeded (exit code $($result.ExitCode))"
}

function Test-ProcessRunning([int] $processId) {
    return $null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)
}

function Get-SystemProxyState {
    if ($IsWindows) {
        $settings = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        $enabled = ($settings.PSObject.Properties['ProxyEnable'] ? $settings.ProxyEnable : 0) -eq 1
        $server = $settings.PSObject.Properties['ProxyServer'] ? "$($settings.ProxyServer)" : ''
        return [pscustomobject]@{
            HttpEnabled  = $enabled
            HttpServer   = $server
            HttpsEnabled = $enabled
            HttpsServer  = $server
        }
    }

    # scutil reports the effective (primary service) configuration that apps resolve.
    $values = @{}
    foreach ($line in (scutil --proxy)) {
        if ($line -match '^\s*(\w+)\s*:\s*(.+?)\s*$') { $values[$Matches[1]] = $Matches[2] }
    }
    return [pscustomobject]@{
        HttpEnabled  = $values['HTTPEnable'] -eq '1'
        HttpServer   = "$($values['HTTPProxy']):$($values['HTTPPort'])"
        HttpsEnabled = $values['HTTPSEnable'] -eq '1'
        HttpsServer  = "$($values['HTTPSProxy']):$($values['HTTPSPort'])"
    }
}

function Show-SystemProxyState([string] $label) {
    Write-Host "  System proxy ($label):"
    (Get-SystemProxyState | Format-List | Out-String).Trim().Split("`n") | ForEach-Object { Write-Host "    $_" }
    if ($IsMacOS) {
        foreach ($service in Get-MacNetworkServices) {
            $web = (networksetup -getwebproxy $service) -join ', '
            $secure = (networksetup -getsecurewebproxy $service) -join ', '
            Write-Host "    [$service] web: $web | secure: $secure"
        }
    }
}

function Get-MacNetworkServices {
    # First line is a legend; disabled services are prefixed with '*'.
    networksetup -listallnetworkservices | Select-Object -Skip 1 | Where-Object { $_ -and -not $_.StartsWith('*') }
}

function Test-SystemProxyRegistered {
    $state = Get-SystemProxyState
    return $state.HttpEnabled -and $state.HttpsEnabled -and
        $state.HttpServer -eq $expectedProxy -and $state.HttpsServer -eq $expectedProxy
}

function Test-SystemProxyRestored {
    $state = Get-SystemProxyState
    # Dev Proxy currently restores by disabling the proxy; previous server values aren't
    # restored yet (https://github.com/dotnet/dev-proxy/pull/2100), so compare on/off state.
    return $state.HttpEnabled -eq $script:baseline.HttpEnabled -and
        $state.HttpsEnabled -eq $script:baseline.HttpsEnabled
}

function Assert-SystemProxyRegistered([string] $context) {
    $ok = Wait-Until { Test-SystemProxyRegistered } 20
    Show-SystemProxyState $context
    Assert-True $ok "system proxy points to $expectedProxy ($context)"
}

function Assert-SystemProxyRestored([string] $context) {
    $ok = Wait-Until { Test-SystemProxyRestored } 20
    Show-SystemProxyState $context
    Assert-True $ok "system proxy restored to its previous on/off state ($context)"
}

function Get-RootCertificatePath {
    # Mirrors CertificateAuthority.ResolveConfigDirectory in DevProxy.Proxy.Kestrel.
    if ($IsWindows) { return Join-Path $AppDir 'rootCert.pfx' }
    return Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'dev-proxy/rootCert.pfx'
}

function Get-RootCertificate {
    $path = Get-RootCertificatePath
    Assert-True (Test-Path $path) "root certificate exists at $path"
    $withKey = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($path, '')
    try {
        # Only the public part is needed to check trust.
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($withKey.Export('Cert'))
    }
    finally {
        $withKey.Dispose()
    }
}

function Assert-RootCertificateTrusted {
    $root = Get-RootCertificate
    Write-Host "  Root: $($root.Subject) ($($root.Thumbprint))"

    if ($IsWindows) {
        foreach ($location in 'CurrentUser', 'LocalMachine') {
            $found = @(Get-ChildItem "Cert:\$location\Root" | Where-Object Thumbprint -eq $root.Thumbprint).Count -gt 0
            Write-Host "  In $location\Root: $found"
        }
        # The CurrentUser\Root view includes LocalMachine\Root, so this is what apps see.
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
        try {
            $store.Open('ReadOnly')
            Assert-True ($store.Certificates.Contains($root)) 'root certificate is trusted for the current user (Root store)'
        }
        finally {
            $store.Dispose()
        }
        return
    }

    $pem = Join-Path $WorkDir 'root.pem'
    Set-Content -Path $pem -Value $root.ExportCertificatePem() -NoNewline
    $hashes = (security find-certificate -a -Z -c $root.GetNameInfo('SimpleName', $false) 2>&1) -join "`n"
    Assert-True ($hashes -match [regex]::Escape($root.Thumbprint)) 'root certificate is in a keychain on the search list'
    $verify = (security verify-cert -c $pem -L 2>&1) -join ' '
    Write-Host "  security verify-cert: $verify"
    Assert-True ($LASTEXITCODE -eq 0) 'root certificate is trusted (security verify-cert)'
}

function Invoke-Probe {
    # Runs in a fresh process so .NET reads the current OS proxy settings: HttpClient's
    # default proxy is resolved once per process. No proxy is configured explicitly.
    $output = & pwsh -NoProfile -NonInteractive -File $script:probeScript -Url $mockUrl -Marker $mockMarker 2>&1 |
        ForEach-Object { "$_" }
    $output | ForEach-Object { Write-Host "    $_" }
    return $LASTEXITCODE -eq 0
}

function Assert-ProbeAnsweredByMock([string] $context) {
    $ok = $false
    for ($attempt = 1; $attempt -le 5 -and -not $ok; $attempt++) {
        Write-Host "  Probe attempt $attempt ($context)"
        $ok = Invoke-Probe
        if (-not $ok) { Start-Sleep -Seconds 2 }
    }
    Assert-True $ok "HTTPS request via OS proxy settings answered by the Dev Proxy mock ($context)"
}

function Get-ReloadCount([string] $logFile) {
    if (-not (Test-Path $logFile)) { return 0 }
    return @(Select-String -Path $logFile -SimpleMatch 'Configuration file changed. Restarting proxy').Count
}

function Show-LogFile([string] $logFile) {
    if ($logFile -and (Test-Path $logFile)) {
        Write-Host "::group::Dev Proxy log ($logFile)"
        Get-Content $logFile | ForEach-Object { Write-Host $_ }
        Write-Host '::endgroup::'
    }
}

function Write-Config([string] $comment) {
    $config = [ordered]@{
        plugins     = @(
            [ordered]@{
                name          = 'MockResponsePlugin'
                enabled       = $true
                pluginPath    = '~appFolder/plugins/DevProxy.Plugins.dll'
                configSection = 'mocksPlugin'
            }
        )
        urlsToWatch = @('https://devproxy-os-integration.example.com/*')
        mocksPlugin = [ordered]@{ mocksFile = 'mocks.json' }
        logLevel    = 'information'
        # Changing this value triggers a hot reload.
        testRun     = $comment
    }
    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $script:configFile
}

#endregion

#region setup

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir | Out-Null
$script:configFile = Join-Path $WorkDir 'devproxyrc.json'
$script:probeScript = Join-Path $WorkDir 'probe.ps1'

Write-Config 'initial'
@{
    mocks = @(
        @{
            request  = @{ url = $mockUrl }
            response = @{ statusCode = 200; body = @{ source = $mockMarker } }
        }
    )
} | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $WorkDir 'mocks.json')

Set-Content -Path $script:probeScript -Value @'
param([string] $Url, [string] $Marker)
$ErrorActionPreference = 'Stop'
$uri = [Uri] $Url
Write-Output "OS proxy for $uri -> $([System.Net.Http.HttpClient]::DefaultProxy.GetProxy($uri))"
# Default handler: UseProxy = true, Proxy = null => HttpClient.DefaultProxy (OS settings),
# default certificate validation => OS trust store.
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(20)
try {
    $response = $client.GetAsync($uri).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Output "HTTP $([int] $response.StatusCode): $body"
    if ($response.IsSuccessStatusCode -and $body.Contains($Marker)) { exit 0 }
    exit 1
}
catch {
    $e = $_.Exception
    while ($e) { Write-Output "$($e.GetType().FullName): $($e.Message)"; $e = $e.InnerException }
    exit 2
}
'@

$script:baseline = Get-SystemProxyState
Show-SystemProxyState 'baseline'
Assert-True (-not (Test-SystemProxyRegistered)) 'Dev Proxy is not registered as the system proxy before the test'

if ($BreakSystemProxy -and $IsMacOS) {
    Write-Host '::warning::BreakSystemProxy: replacing toggle-proxy.sh with a no-op'
    Set-Content -Path (Join-Path $AppDir 'toggle-proxy.sh') -Value "#!/bin/bash`nexit 0`n"
}

#endregion

$logFile = $null
try {
    if ($IsWindows) {
        # Installing into the CurrentUser root store shows a modal confirmation dialog that
        # hangs unattended runs. Hosted runners are elevated, so trust the root in the
        # LocalMachine store (no dialog). The CurrentUser root view includes LocalMachine
        # roots, so Dev Proxy's own install step below sees it as trusted and skips the add.
        Write-Step 'Windows: mint the root certificate and trust it machine-wide'
        $mint = Start-DevProxy -asSystemProxy $false -installCert $false
        Stop-DevProxy
        Assert-True (Wait-Until { -not (Test-ProcessRunning $mint.pid) } 30) 'minting instance stopped'
        $cer = Join-Path $WorkDir 'root.cer'
        [IO.File]::WriteAllBytes($cer, (Get-RootCertificate).Export('Cert'))
        Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }
    else {
        # On first run Dev Proxy trusts the root in the login keychain with
        # `security add-trusted-cert` (user trust domain). That waits for an authorization
        # UI that never appears on hosted runners and hangs indefinitely, even after
        # relaxing com.apple.trust-settings.user. Trust the root in the admin domain
        # (System keychain, sudo works unattended) and mark first run as done so Dev Proxy
        # takes its regular subsequent-run path.
        Write-Step 'macOS: mint the root certificate and trust it system-wide'
        $mint = Start-DevProxy -asSystemProxy $false -installCert $false
        Stop-DevProxy
        Assert-True (Wait-Until { -not (Test-ProcessRunning $mint.pid) } 30) 'minting instance stopped'
        $pem = Join-Path $WorkDir 'root.pem'
        Set-Content -Path $pem -Value (Get-RootCertificate).ExportCertificatePem() -NoNewline
        sudo perl -e 'alarm 120; exec @ARGV' security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain $pem
        Assert-True ($LASTEXITCODE -eq 0) "trusted root certificate in the System keychain (exit code $LASTEXITCODE)"
        New-Item -ItemType File -Force -Path (Join-Path $AppDir '.hasrun') | Out-Null
    }

    Write-Step 'Start Dev Proxy as system proxy with certificate install'
    $instance = Start-DevProxy -asSystemProxy $true -installCert $true
    $logFile = $instance.logFile
    Assert-True (Test-ProcessRunning $instance.pid) "Dev Proxy is running (PID $($instance.pid))"

    if ($BreakSystemProxy -and $IsWindows) {
        Write-Host '::warning::BreakSystemProxy: clearing ProxyEnable after registration'
        Start-Sleep -Seconds 2
        Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyEnable -Value 0
    }

    Write-Step 'Assert OS state'
    Assert-SystemProxyRegistered 'after start'
    Assert-RootCertificateTrusted

    Write-Step 'Request through OS proxy settings (no explicit proxy)'
    Assert-ProbeAnsweredByMock 'after start'

    Write-Step 'Hot reload keeps the system proxy registered'
    $reloads = Get-ReloadCount $logFile
    # Sample the OS proxy continuously from before the config change until well after the
    # restart, so a transient unregister/re-register (https://github.com/dotnet/dev-proxy/pull/2149)
    # is caught even if the proxy is registered again by the end.
    $outages = [System.Collections.Generic.List[string]]::new()
    $reloadSeenAt = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    Write-Config 'reloaded'
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-SystemProxyRegistered)) {
            $state = Get-SystemProxyState
            $outages.Add("$([DateTime]::UtcNow.ToString('HH:mm:ss.fff')) http=$($state.HttpEnabled)/$($state.HttpServer) https=$($state.HttpsEnabled)/$($state.HttpsServer)")
        }
        if (-not $reloadSeenAt -and (Get-ReloadCount $logFile) -gt $reloads) {
            $reloadSeenAt = [DateTime]::UtcNow
            # Keep watching while the proxy restarts.
            $deadline = $reloadSeenAt.AddSeconds(15)
        }
        Start-Sleep -Milliseconds 100
    }
    Assert-True ($null -ne $reloadSeenAt) 'config change triggered a hot reload'
    Assert-True (Wait-Until { Test-ProcessRunning $instance.pid } 5) 'Dev Proxy is still running after hot reload'
    $registeredAtEnd = Test-SystemProxyRegistered
    Show-SystemProxyState 'after hot reload'
    foreach ($outage in $outages | Select-Object -First 20) { Write-Host "  Not registered at $outage" }
    $hotReloadOk = $registeredAtEnd -and $outages.Count -eq 0
    if ($hotReloadOk) {
        Write-Host "  PASS: system proxy stayed registered at $expectedProxy throughout hot reload" -ForegroundColor Green
        Assert-ProbeAnsweredByMock 'after hot reload'
    }
    elseif ($StrictHotReload) {
        Assert-True $false "system proxy stays registered throughout hot reload ($($outages.Count) unregistered samples, registered at end: $registeredAtEnd)"
    }
    else {
        Write-Host "::warning::System proxy didn't stay registered throughout hot reload ($($outages.Count) unregistered samples, registered at end: $registeredAtEnd). Known issue fixed by https://github.com/dotnet/dev-proxy/pull/2149; set STRICT_HOT_RELOAD to true in os-integration.yml once it is merged."
        if ($registeredAtEnd) { Assert-ProbeAnsweredByMock 'after hot reload' }
    }

    Write-Step 'devproxy stop restores the system proxy'
    Stop-DevProxy
    Assert-True (Wait-Until { -not (Test-ProcessRunning $instance.pid) } 30) 'Dev Proxy process exited'
    Assert-SystemProxyRestored 'after devproxy stop'
    Show-LogFile $logFile
    $logFile = $null

    Write-Step 'Crash: system proxy is restored after the process is killed'
    $instance = Start-DevProxy -asSystemProxy $true -installCert $true
    $logFile = $instance.logFile
    Assert-SystemProxyRegistered 'before crash'
    Stop-Process -Id $instance.pid -Force
    Assert-True (Wait-Until { -not (Test-ProcessRunning $instance.pid) } 30) "killed Dev Proxy (PID $($instance.pid))"
    Assert-True (Test-SystemProxyRegistered) 'system proxy is left registered after the crash (precondition)'

    # Detect the command from help output; probing it directly could start a proxy on
    # builds that don't know it.
    $hasReset = @((Invoke-DevProxy @('--help') -Quiet).Output | Where-Object { $_ -match '^\s+system-proxy\b' }).Count -gt 0
    if ($hasReset) {
        $reset = Invoke-DevProxy @('system-proxy', 'reset')
        Assert-True ($reset.ExitCode -eq 0) 'devproxy system-proxy reset succeeded'
        Assert-SystemProxyRestored 'after devproxy system-proxy reset'
    }
    else {
        Write-Host "::notice::'devproxy system-proxy reset' isn't available in this build (https://github.com/dotnet/dev-proxy/pull/2100); covering crash recovery via 'devproxy stop' orphan reconciliation instead."
        Stop-DevProxy
        Assert-SystemProxyRestored 'after devproxy stop (orphan reconciliation)'
    }

    Write-Host ''
    Write-Host 'All OS integration assertions passed.' -ForegroundColor Green
}
finally {
    Show-LogFile $logFile
    # Best effort: leave the runner tidy so log collection isn't affected by a live proxy.
    try { Invoke-DevProxy @('stop') | Out-Null } catch { Write-Host "Cleanup failed: $_" }
}
