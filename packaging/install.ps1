# Installs PrintInterceptor for a till: virtual printer, configuration, ACLs and
# either the Windows service or tray autostart. Also removes everything with
# -Uninstall.
#
# Run PowerShell as Administrator. This script is exercised on Windows only;
# it is not runnable on the Linux build host (see packaging/README.md).
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$ExePath = "",
    [ValidateSet("Service", "Tray")]
    [string]$RunMode = "Service",
    [string]$ServiceName = "PrintInterceptor",
    [string]$DisplayName = "FiscalStack Print Interceptor",

    [ValidateSet("Text", "PostScript")]
    [string]$PrinterMode = "Text",
    [string]$PrinterName = "FiscalStack Fiscal Printer",
    [string]$HostAddress = "127.0.0.1",
    [int]$Port = 9100,
    [switch]$SkipPrinter,

    [string]$ConfigPath = "",
    [string]$ApiEndpoint = "",
    [string]$ApiKey = "",
    [string]$GhostscriptPath = "",
    [string]$ReceiptPrinter = "",
    [string]$A4Printer = "",
    [string]$TrayUser = "",
    [switch]$SkipAutostart,
    [switch]$StartApp,

    [switch]$Uninstall,
    [switch]$Purge
)

$ErrorActionPreference = "Stop"

$runKeyPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "PrintInterceptorTray"
$defaultRoot = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor"
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $defaultRoot "config.json"
}
# The service and the Run key start the exe with its own working directory, so a
# relative path here would resolve against System32. Always store it absolute.
$ConfigPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
$dataDirectory = Join-Path $defaultRoot "data"

function Resolve-SiblingScript([string]$name) {
    $candidates = @(
        (Join-Path $PSScriptRoot $name),
        (Join-Path (Split-Path $PSScriptRoot -Parent) $name)
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    throw "Required script '$name' was not found next to $PSScriptRoot."
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-RestrictedAcl([string]$path, [string]$user) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    # Well-known SIDs keep this working on non-English Windows.
    $grants = @(
        "*S-1-5-32-544:(OI)(CI)F",   # Administrators
        "*S-1-5-18:(OI)(CI)F"        # SYSTEM
    )
    if (-not [string]::IsNullOrWhiteSpace($user)) {
        $grants += "$user:(OI)(CI)F"
    }

    & icacls.exe $path /inheritance:r /grant:r @grants /T | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict access to $path (icacls exit $LASTEXITCODE)."
    }
}

function Read-ConfigFile([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    return Get-Content -Path $path -Raw | ConvertFrom-Json
}

function Ensure-Property($object, [string]$name) {
    if ($null -eq $object.PSObject.Properties[$name]) {
        $object | Add-Member -NotePropertyName $name -NotePropertyValue $null
    }
}

function Write-ConfigFile([string]$path, $config) {
    $directory = Split-Path $path -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $config | ConvertTo-Json -Depth 12
    Set-Content -Path $path -Value $json -Encoding UTF8
}

function Stop-And-RemoveService([string]$name) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Host "Service '$name' is not installed."
        return
    }

    if ($service.Status -ne "Stopped") {
        & sc.exe stop $name | Out-Null
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Service -Name $name -ErrorAction SilentlyContinue).Status -ne "Stopped" -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }

    & sc.exe delete $name | Out-Null

    # sc.exe marks the service for deletion; wait until it is gone so an upgrade
    # can create it again immediately.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service -Name $name -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Write-Warning "Service '$name' is still pending deletion; a reinstall may need to be retried."
    }
    else {
        Write-Host "Removed service '$name'."
    }
}

function Remove-Autostart {
    foreach ($hive in @("HKLM:\Software\Microsoft\Windows\CurrentVersion\Run", "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run")) {
        if (Test-Path $hive) {
            Remove-ItemProperty -Path $hive -Name $runValueName -ErrorAction SilentlyContinue
        }
    }
}

if ($Uninstall) {
    if (-not (Test-Elevated)) {
        throw "Uninstall requires an elevated PowerShell prompt."
    }

    Stop-And-RemoveService $ServiceName

    if (-not $SkipPrinter) {
        $addPrinter = Resolve-SiblingScript "addprinter.ps1"
        & $addPrinter -PrinterName $PrinterName -Port $Port -Remove
    }

    Remove-Autostart

    if ($Purge) {
        $machineKey = [Environment]::GetEnvironmentVariable("PRINTINTERCEPTOR_API_KEY", "Machine")
        if ($null -ne $machineKey) {
            [Environment]::SetEnvironmentVariable("PRINTINTERCEPTOR_API_KEY", $null, "Machine")
        }

        if (Test-Path $defaultRoot) {
            Remove-Item -Path $defaultRoot -Recurse -Force
            Write-Host "Removed configuration and data at $defaultRoot."
        }
    }
    else {
        Write-Host "Configuration and fiscal data were kept at $defaultRoot (use -Purge to delete them)."
    }

    Write-Host "PrintInterceptor removed."
    exit 0
}

# ─── Install ──────────────────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path (Split-Path $PSScriptRoot -Parent) "publish\PrintInterceptor.exe"
}
if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath. Publish first (see packaging\build-release.ps1)."
}
$exe = (Resolve-Path $ExePath).Path

Write-Host "Installing PrintInterceptor from $exe (mode: $RunMode)."

# 1. Configuration. The app creates a complete default file on first run; the
#    installer only writes the values it was given so re-installs keep edits.
$config = Read-ConfigFile $ConfigPath
if ($null -eq $config) {
    $config = [pscustomobject]@{
        listenPort = $Port
        apiEndpoint = "https://fiscalstack.co.zw/api/v1/"
        apiKey = ""
        setupCompleted = $false
        printing = [pscustomobject]@{ printerName = ""; a4PrinterName = "" }
    }
}

Ensure-Property $config "listenPort"
Ensure-Property $config "apiEndpoint"
Ensure-Property $config "apiKey"
Ensure-Property $config "ghostscriptPath"
Ensure-Property $config "printing"
if ($null -eq $config.printing) {
    $config.printing = [pscustomobject]@{}
}
Ensure-Property $config.printing "printerName"
Ensure-Property $config.printing "a4PrinterName"

# build-release.ps1 bundles Ghostscript into the release by default, so a till installed
# from that package already has {app}\ghostscript. An explicit -GhostscriptPath always
# wins, and so does a config value that still points at a working install, so a choice
# made once survives a re-install.
if ([string]::IsNullOrWhiteSpace($GhostscriptPath)) {
    $bundledGhostscript = Join-Path (Split-Path $exe -Parent) "ghostscript\bin\gswin64c.exe"
    $configuredGhostscript = if ($null -eq $config.ghostscriptPath) { "" } else { [string]$config.ghostscriptPath }
    if ((Test-Path $bundledGhostscript) -and
        ([string]::IsNullOrWhiteSpace($configuredGhostscript) -or -not (Test-Path $configuredGhostscript))) {
        $GhostscriptPath = $bundledGhostscript
    }
}

$config.listenPort = $Port
if (-not [string]::IsNullOrWhiteSpace($ApiEndpoint)) { $config.apiEndpoint = $ApiEndpoint.Trim() }
if (-not [string]::IsNullOrWhiteSpace($GhostscriptPath)) { $config.ghostscriptPath = $GhostscriptPath.Trim() }
if (-not [string]::IsNullOrWhiteSpace($ReceiptPrinter)) { $config.printing.printerName = $ReceiptPrinter.Trim() }
if (-not [string]::IsNullOrWhiteSpace($A4Printer)) { $config.printing.a4PrinterName = $A4Printer.Trim() }

if (-not [string]::IsNullOrWhiteSpace($config.storage) -and
    -not [string]::IsNullOrWhiteSpace($config.storage.dataDirectory)) {
    $dataDirectory = $config.storage.dataDirectory
}

Write-ConfigFile $ConfigPath $config
Write-Host "Configuration: $ConfigPath"

# The receipt layouts ship next to the executable ({app}\profiles). A package built
# without them leaves every till with the built-in rules only and nothing to import.
$bundledProfiles = Join-Path (Split-Path $exe -Parent) "profiles"
if (Test-Path (Join-Path $bundledProfiles "sage.json")) {
    $profileCount = (Get-ChildItem -Path $bundledProfiles -Filter "*.json").Count
    Write-Host "Parse profiles: $profileCount shipped in $bundledProfiles"
}
else {
    Write-Warning ("No parse profiles found in $bundledProfiles. Rebuild the release so the profiles " +
        "folder ships with PrintInterceptor (source: profiles/ in the repository).")
}

# Keep the secret out of config.json: the app reads this variable at startup.
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    [Environment]::SetEnvironmentVariable("PRINTINTERCEPTOR_API_KEY", $ApiKey.Trim(), "Machine")
    Write-Host "Stored the API key in the machine environment (PRINTINTERCEPTOR_API_KEY)."
}

$aclUser = $null
if ($RunMode -eq "Tray") {
    $aclUser = if ([string]::IsNullOrWhiteSpace($TrayUser)) {
        "$env:USERDOMAIN\$env:USERNAME"
    }
    else {
        $TrayUser
    }
}
Set-RestrictedAcl (Split-Path $ConfigPath -Parent) $aclUser
Set-RestrictedAcl $dataDirectory $aclUser
Write-Host "Restricted ACLs on the configuration and data folders."

# 2. Virtual printer.
if ($SkipPrinter) {
    Write-Host "Skipping the virtual printer."
}
else {
    $addPrinter = Resolve-SiblingScript "addprinter.ps1"
    & $addPrinter -PrinterName $PrinterName -HostAddress $HostAddress -Port $Port -Mode $PrinterMode
}

if ($PrinterMode -eq "PostScript") {
    $gswin = $null
    if (-not [string]::IsNullOrWhiteSpace($config.ghostscriptPath) -and (Test-Path $config.ghostscriptPath)) {
        $gswin = $config.ghostscriptPath
    }
    elseif (Get-Command "gswin64c.exe" -ErrorAction SilentlyContinue) {
        $gswin = (Get-Command "gswin64c.exe").Source
    }
    else {
        foreach ($candidate in @("$env:ProgramFiles\gs", "${env:ProgramFiles(x86)}\gs")) {
            if (Test-Path $candidate) {
                $found = Get-ChildItem -Path $candidate -Recurse -Filter "gswin64c.exe" -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($found) { $gswin = $found.FullName; break }
            }
        }
    }

    if (-not $gswin) {
        Write-Warning ("PostScript mode needs Ghostscript (AGPL) and none was found. Install 64-bit " +
            "Ghostscript and set ghostscriptPath in $ConfigPath, pass -GhostscriptPath here, or rebuild the " +
            "release without -NoGhostscript so the package carries its own copy.")
    }
    else {
        Write-Host "Ghostscript: $gswin"
    }
}

# 3. Run mode. Clear the previous service first so upgrades and service<->tray
#    switches do not fail with "service already exists".
Remove-Autostart

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-And-RemoveService $ServiceName
}

if ($RunMode -eq "Service") {
    $installService = Resolve-SiblingScript "install-service.ps1"
    & $installService -ExePath $exe -ServiceName $ServiceName -DisplayName $DisplayName -ConfigPath $ConfigPath

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "Service '$ServiceName' was not created."
    }

    if ($service.Status -ne "Running") {
        & sc.exe start $ServiceName | Out-Null
    }

    Write-Host "Service '$ServiceName' is installed and running."
}
else {
    if (-not $SkipAutostart) {
        if (-not (Test-Path $runKeyPath)) {
            New-Item -Path $runKeyPath -Force | Out-Null
        }

        $command = "`"$exe`" --tray --config `"$ConfigPath`""
        New-ItemProperty -Path $runKeyPath -Name $runValueName -Value $command -PropertyType String -Force | Out-Null
        Write-Host "Tray mode starts at sign-in for all users (HKLM Run)."
    }

    # Only when asked: a silent/deployment install must not start a second
    # listener in an invisible session. The Run key covers the next sign-in.
    if ($StartApp) {
        Start-Process -FilePath $exe -ArgumentList "-tray --config `"$ConfigPath`""
        Write-Host "Started the tray app."
    }
    else {
        Write-Host "Start '$exe --tray' (or sign in again) to show the tray icon."
    }
}

Write-Host ""
Write-Host "PrintInterceptor is installed."
Write-Host "  Virtual printer : $(if ($SkipPrinter) { 'skipped' } else { $PrinterName })"
Write-Host "  Listen port     : $Port"
Write-Host "  Configuration   : $ConfigPath"
Write-Host "  Data and logs   : $dataDirectory"
Write-Host "Open the tray menu and choose 'Fiscal status…' to verify the device and fiscal day."
