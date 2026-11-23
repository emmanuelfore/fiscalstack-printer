# Checks a signed release manifest, verifies the signed installer, drains the
# fiscal process through PrintInterceptor.exe --status, backs up, upgrades the
# till silently, health-checks the result and rolls back on failure.
#
# Install the scheduled task with packaging\install-update-task.ps1. This script
# itself requires elevation (SYSTEM is fine) and never handles API keys.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$ManifestUrl = "https://updates.fiscalstack.co.zw/printinterceptor/update.json",
    [Parameter(Mandatory = $true)]
    [string]$SignerThumbprint,
    [string]$Channel = "stable",
    [string]$ServiceName = "PrintInterceptor",
    [string]$AppDirectory = "",
    [string]$ConfigPath = "",
    [string]$InstallTasks = "printer,ghostscript",
    [switch]$AllowTrayUpdate,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Normalize-Thumbprint {
    param([string]$Value)
    return (($Value -replace '[^A-Fa-f0-9]', '').ToUpperInvariant())
}

function ConvertTo-VersionObject {
    param([string]$Value)

    $clean = [string]$Value
    $dash = $clean.IndexOf('-')
    if ($dash -ge 0) { $clean = $clean.Substring(0, $dash) }

    $parts = @()
    foreach ($piece in $clean.Split('.')) {
        $number = 0
        if ([int]::TryParse($piece, [ref]$number)) { $parts += $number } else { $parts += 0 }
    }
    while ($parts.Count -lt 4) { $parts += 0 }
    if ($parts.Count -gt 4) { $parts = @($parts[0..3]) }

    return (New-Object Version -ArgumentList $parts[0], $parts[1], $parts[2], $parts[3])
}

function Resolve-SetupUrl {
    param([string]$ManifestUri, [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "The update manifest has no setupUrl."
    }

    $candidate = $null
    try { $candidate = New-Object Uri -ArgumentList $Value } catch { $candidate = $null }

    if ($null -ne $candidate -and $candidate.IsAbsoluteUri) {
        if ($candidate.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "Refusing a non-HTTPS setupUrl: $Value"
        }
        return $candidate.AbsoluteUri
    }

    $baseUri = New-Object Uri -ArgumentList $ManifestUri
    if ($baseUri.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "Refusing a non-HTTPS manifest URL: $ManifestUri"
    }

    $resolved = New-Object Uri -ArgumentList $baseUri, $Value
    if ($resolved.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "The resolved setup URL is not HTTPS: $($resolved.AbsoluteUri)"
    }
    return $resolved.AbsoluteUri
}

function Set-RestrictedAcl {
    param([string]$Path, [string]$User)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    # (OI)(CI) is valid on directories only. Applying it with icacls /T to a
    # file after /inheritance:r leaves that file with an empty DACL, so grant
    # each object with the flags its type allows.
    $targets = @(Get-Item -LiteralPath $Path -Force)
    if ($targets[0].PSIsContainer) {
        $targets += @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop)
    }
    $targets = @($targets | Sort-Object { $_.FullName.Length })

    foreach ($target in $targets) {
        $flags = if ($target.PSIsContainer) { "(OI)(CI)" } else { "" }
        $grants = @(
            "*S-1-5-32-544:${flags}F",  # Administrators
            "*S-1-5-18:${flags}F"       # SYSTEM
        )
        if (-not [string]::IsNullOrWhiteSpace($User)) {
            $grants += "${User}:${flags}F"
        }

        & icacls.exe $target.FullName /reset | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not reset ACLs at $($target.FullName) (icacls exit $LASTEXITCODE)." }

        & icacls.exe $target.FullName /inheritance:r /grant:r @grants | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restrict ACLs at $($target.FullName) (icacls exit $LASTEXITCODE)." }
    }
}

function Write-UpdateEvent {
    param([string]$Message, [string]$Level = "Information", [int]$EventId = 700)

    try {
        if (-not (Get-EventLog -LogName Application -Source "PrintInterceptor" -ErrorAction SilentlyContinue)) {
            New-EventLog -LogName Application -Source "PrintInterceptor" -ErrorAction Stop | Out-Null
        }
        Write-EventLog -LogName Application -Source "PrintInterceptor" -EntryType $Level `
            -EventId $EventId -Message $Message -ErrorAction Stop
    }
    catch {
        # Diagnostics must never turn a successful update into a failure.
    }
}

function Copy-IfExists {
    param([string]$Source, [string]$Destination)

    if (Test-Path $Source) {
        Copy-Item -Path $Source -Destination $Destination -Recurse -Force
    }
}

function Get-Status {
    param([string]$Executable)

    if (-not (Test-Path $Executable)) {
        throw "Application executable not found: $Executable"
    }

    # The app is a WinExe, so the PowerShell call operator neither waits nor
    # captures stdout reliably (especially from the SYSTEM scheduled task).
    # Start it with redirected streams and wait explicitly before gating an
    # upgrade on the snapshot it prints.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = '--status --json --service-name="' + $ServiceName + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = Split-Path $Executable -Parent

    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if (-not $process.WaitForExit(30000)) {
            throw "PrintInterceptor --status did not exit within 30 seconds."
        }
        if ($process.ExitCode -ne 0) {
            throw "PrintInterceptor --status failed (exit $($process.ExitCode)): $stdout $stderr"
        }
        if ([string]::IsNullOrWhiteSpace($stdout)) {
            throw "PrintInterceptor --status produced no output: $stderr"
        }

        return ($stdout | ConvertFrom-Json)
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

$root = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor"
$updatesDir = Join-Path $root "updates"
if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    $AppDirectory = Join-Path ${env:ProgramFiles} "FiscalStack\PrintInterceptor"
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $root "config.json"
}
if (-not (Test-Path $ConfigPath)) {
    throw "Configuration not found at $ConfigPath. Install PrintInterceptor before enabling updates."
}

$config = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json
$dataDir = Join-Path $root "data"
if ($null -ne $config.storage -and -not [string]::IsNullOrWhiteSpace([string]$config.storage.dataDirectory)) {
    $dataDir = [string]$config.storage.dataDirectory
}
$secretDir = Join-Path $root "secrets"
$profilesDir = Join-Path $root "profiles"
$exe = Join-Path $AppDirectory "PrintInterceptor.exe"
$expectedThumbprint = Normalize-Thumbprint $SignerThumbprint
if ($expectedThumbprint.Length -lt 40) {
    throw "Signer thumbprint must be the SHA-1 code-signing certificate thumbprint."
}

Set-RestrictedAcl $updatesDir ""

$manifestResponse = Invoke-WebRequest -Uri $ManifestUrl -UseBasicParsing -TimeoutSec 30
$manifest = $manifestResponse.Content | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) {
    throw "The update manifest has no version."
}
if (-not [string]::IsNullOrWhiteSpace([string]$manifest.channel) -and
    [string]$manifest.channel -ne $Channel) {
    throw "Manifest channel '$($manifest.channel)' does not match requested channel '$Channel'."
}
if (-not [string]::IsNullOrWhiteSpace([string]$manifest.signerThumbprint)) {
    $manifestThumbprint = Normalize-Thumbprint ([string]$manifest.signerThumbprint)
    if ($manifestThumbprint -ne $expectedThumbprint) {
        throw "Manifest signer thumbprint does not match the pinned code-signing certificate."
    }
}

$targetVersion = [string]$manifest.version
$setupUrl = Resolve-SetupUrl -ManifestUri $ManifestUrl -Value ([string]$manifest.setupUrl)
$expectedHash = ([string]$manifest.sha256).ToLowerInvariant().Replace(" ", "")
if ($expectedHash -notmatch '^[a-f0-9]{64}$') {
    throw "The update manifest sha256 is not a 64-character hex digest."
}

$currentVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($currentVersion)) {
    $currentVersion = (Get-Item $exe).VersionInfo.FileVersion
}
if ([string]::IsNullOrWhiteSpace($currentVersion)) { $currentVersion = "0.0.0.0" }

$comparison = (ConvertTo-VersionObject $targetVersion).CompareTo((ConvertTo-VersionObject $currentVersion))
if ($comparison -le 0 -and -not $Force) {
    Write-Host "Installed $currentVersion is already at or above manifest version $targetVersion."
    Write-UpdateEvent "PrintInterceptor update skipped: installed $currentVersion, manifest $targetVersion."
    exit 0
}

$status = Get-Status -Executable $exe
if (-not $status.healthy -and -not $Force) {
    $reason = "pendingOffline=$($status.pendingOffline) recoveryPending=$($status.recoveryPending) " +
              "offlinePaused=$($status.offlinePaused) staleInFlight=$($status.staleInFlight) " +
              "attentionRequired=$($status.attentionRequired) listenerReady=$($status.listenerReady) " +
              "serviceState=$($status.serviceState) ledgerError=$($status.ledgerError)"
    throw "PrintInterceptor is not healthy; refusing to update. $reason"
}

$serviceMode = ([string]$status.runMode -eq "service")
if (-not $serviceMode -and -not $AllowTrayUpdate) {
    throw "This till is in tray mode. Silent updates cannot stop the interactive tray; use -AllowTrayUpdate only with an operator plan, or reinstall in service mode."
}
if ($serviceMode -and -not [string]::IsNullOrEmpty([string]$status.serviceName) -and
    -not [string]::IsNullOrEmpty([string]$status.serviceState) -and
    [string]$status.serviceState -ine "RUNNING" -and -not $Force) {
    throw "Service '$($status.serviceName)' is $($status.serviceState), not RUNNING."
}

$setupPath = Join-Path $updatesDir "PrintInterceptor-$targetVersion-setup.exe"
if (Test-Path $setupPath) { Remove-Item $setupPath -Force }
Invoke-WebRequest -Uri $setupUrl -OutFile $setupPath -UseBasicParsing -TimeoutSec 300

$actualHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "Installer SHA256 mismatch. Expected $expectedHash, got $actualHash."
}

$signature = Get-AuthenticodeSignature -FilePath $setupPath
if ($signature.Status -ne "Valid") {
    throw "Installer Authenticode signature is $($signature.Status): $($signature.StatusMessage)"
}
$actualThumbprint = Normalize-Thumbprint ([string]$signature.SignerCertificate.Thumbprint)
if ($actualThumbprint -ne $expectedThumbprint) {
    throw "Installer was signed by $actualThumbprint, not the pinned $expectedThumbprint."
}

if ($DryRun) {
    Write-Host "DRY RUN: $currentVersion -> $targetVersion from $setupUrl"
    Write-Host "Signature valid ($actualThumbprint); SHA256 $actualHash; status healthy=$($status.healthy)."
    Write-Host "No service was stopped and nothing was installed."
    exit 0
}

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$logDir = Join-Path $dataDir "logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$backupDir = Join-Path $updatesDir "backup-$currentVersion-$stamp"
$script:backupDir = $backupDir
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
Set-RestrictedAcl $backupDir ""

Copy-Item -Path $ConfigPath -Destination (Join-Path $backupDir "config.json") -Force
Copy-IfExists -Source $dataDir -Destination (Join-Path $backupDir "data")
Copy-IfExists -Source $secretDir -Destination (Join-Path $backupDir "secrets")
Copy-IfExists -Source $profilesDir -Destination (Join-Path $backupDir "profiles")
Copy-IfExists -Source $AppDirectory -Destination (Join-Path $backupDir "app")

& icacls.exe $root /save (Join-Path $backupDir "root-acl.txt") /T | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not save the pre-update ACLs (icacls exit $LASTEXITCODE)."
}

$backupInfo = [ordered]@{
    version      = $currentVersion
    createdAt    = (Get-Date).ToUniversalTime().ToString("o")
    appDirectory = $AppDirectory
    configPath   = $ConfigPath
    dataDirectory = $dataDir
    serviceName  = $ServiceName
    manifestUrl  = $ManifestUrl
}
$backupInfo | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $backupDir "backup.json") -Encoding UTF8

try {
    if ($serviceMode) {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($service.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne "Stopped" -and
               (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne "Stopped") {
            throw "Service '$ServiceName' did not stop within 30 seconds."
        }
    }

    # The manifest is authoritative: a release built with -NoGhostscript has no
    # ghostscript task, so passing the default here would ask Inno Setup to
    # select a task that does not exist in that setup.
    $manifestTasks = ""
    if ($null -ne $manifest.PSObject.Properties["installTasks"]) {
        $manifestTasks = [string]$manifest.installTasks
    }
    $baseTasks = if ([string]::IsNullOrWhiteSpace($manifestTasks)) { $InstallTasks } else { $manifestTasks }
    if ($serviceMode) {
        $installerTasks = $baseTasks
    }
    else {
        # Tray mode is a run-mode choice, not a release component: keep every
        # component from the manifest and add/replace only traymode.
        $taskList = @($baseTasks.Split(',') | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne "traymode"
        })
        $taskList += "traymode"
        $installerTasks = $taskList -join ","
    }
    $logPath = Join-Path $logDir "update-$stamp.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS " +
                 "/TASKS=`"$installerTasks`" /LOG=`"$logPath`""
    $previousConfigEnvironment = [Environment]::GetEnvironmentVariable("PRINTINTERCEPTOR_CONFIG")
    [Environment]::SetEnvironmentVariable("PRINTINTERCEPTOR_CONFIG", $ConfigPath)
    try {
        $installer = Start-Process -FilePath $setupPath -ArgumentList $arguments `
            -Wait -PassThru -NoNewWindow
    }
    finally {
        [Environment]::SetEnvironmentVariable("PRINTINTERCEPTOR_CONFIG", $previousConfigEnvironment)
    }
    if ($installer.ExitCode -ne 0) {
        throw "Installer exited with code $($installer.ExitCode). See $logPath."
    }

    if ($serviceMode) {
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Seconds 1
            $running = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -eq "Running"
        } while (-not $running -and (Get-Date) -lt $deadline)
        if (-not $running) {
            throw "Service '$ServiceName' did not return to RUNNING after the upgrade. See $logPath."
        }
    }

    $after = Get-Status -Executable $exe
    if ((ConvertTo-VersionObject ([string]$after.version)).CompareTo((ConvertTo-VersionObject $targetVersion)) -lt 0) {
        throw "Version verification failed: expected $targetVersion or newer, got $($after.version)."
    }
    if ($serviceMode) {
        if (-not $after.listenerReady) {
            throw "Capture listener is not ready after the upgrade."
        }
        if (-not $after.healthy -and -not $Force) {
            throw "Post-upgrade health check failed: healthy=$($after.healthy)."
        }
    }

    Write-Host "Updated PrintInterceptor from $currentVersion to $($after.version). Backup: $backupDir"
    Write-UpdateEvent "PrintInterceptor updated from $currentVersion to $($after.version); backup at $backupDir."
}
catch {
    $failure = $_.Exception.Message
    Write-UpdateEvent "PrintInterceptor update to $targetVersion failed: $failure" -Level "Error" -EventId 702
    if ($null -ne $script:backupDir -and (Test-Path $script:backupDir)) {
        $rollback = Join-Path $PSScriptRoot "rollback.ps1"
        Write-Warning "Rolling back from $($script:backupDir): $failure"
        & $rollback -BackupPath $script:backupDir -AppDirectory $AppDirectory -ServiceName $ServiceName
    }
    throw
}
