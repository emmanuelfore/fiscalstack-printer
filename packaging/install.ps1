# Installs PrintInterceptor for a till: virtual printer, configuration, ACLs and
# either the Windows service or tray autostart. Also removes everything with
# -Uninstall.
#
# Run PowerShell as Administrator. This script is exercised on Windows only;
# it is not runnable on the Linux build host (see packaging/README.md).
#
# Register the API key after installing, interactively and without putting it on
# any command line:
#   .\publish\PrintInterceptor.exe --register-key
# or open Settings in the tray app. -ApiKey is deprecated: it writes the key to
# the machine environment (registry-readable) and is kept only for compatibility.
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
    # Deprecated: use "PrintInterceptor.exe --register-key" or the tray Settings.
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
    # The updater runs the setup silently without a wizard page for this value,
    # so it passes the till's configured path through the process environment.
    # The application resolves the same variable at runtime.
    $fromEnvironment = [Environment]::GetEnvironmentVariable("PRINTINTERCEPTOR_CONFIG")
    if ([string]::IsNullOrWhiteSpace($fromEnvironment)) {
        $ConfigPath = Join-Path $defaultRoot "config.json"
    }
    else {
        $ConfigPath = $fromEnvironment
    }
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

    # icacls /T applies the same ACE to directories *and* files. (OI)(CI) is
    # only valid on a directory: passing it to a file makes icacls fail that
    # file, and because /inheritance:r has already removed its inherited ACEs
    # the file ends up with an empty DACL that even Administrators cannot read.
    # Apply the right flags per object instead.
    $targets = @(Get-Item -LiteralPath $path -Force)
    if ($targets[0].PSIsContainer) {
        $targets += @(Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction Stop)
    }
    $targets = @($targets | Sort-Object { $_.FullName.Length })

    foreach ($target in $targets) {
        $flags = if ($target.PSIsContainer) { "(OI)(CI)" } else { "" }

        # Well-known SIDs keep this working on non-English Windows.
        $grants = @(
            "*S-1-5-32-544:${flags}F",   # Administrators
            "*S-1-5-18:${flags}F"        # SYSTEM
        )
        if (-not [string]::IsNullOrWhiteSpace($user)) {
            # ${} because "$user:(OI)..." would parse as a drive-qualified variable
            # reference and a parse error here stops the whole script from running.
            $grants += "${user}:${flags}F"
        }

        # Clear every explicit ACE first, including a tray user left by a previous
        # install. /grant:r only replaces grants for the trustees named here, so a
        # Service <- Tray switch would otherwise keep the old user's grant.
        & icacls.exe $target.FullName /reset | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not reset ACLs at $($target.FullName) (icacls exit $LASTEXITCODE)."
        }

        & icacls.exe $target.FullName /inheritance:r /grant:r @grants | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict access to $($target.FullName) (icacls exit $LASTEXITCODE)."
        }
    }
}

function Read-ConfigFile([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try {
        $raw = Get-Content -Path $path -Raw -ErrorAction Stop
        return $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        # A torn or zero-filled config must not stop a re-install. Keep it for
        # diagnosis and let the caller write the default values it was given.
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
        $backup = "$path.corrupt-$stamp"
        for ($i = 2; Test-Path -LiteralPath $backup; $i++) {
            $backup = "$path.corrupt-$stamp-$i"
        }
        Copy-Item -LiteralPath $path -Destination $backup -ErrorAction Stop
        Write-Warning "Configuration at $path could not be read. A copy was saved to $backup; setup will write a fresh default config."
        return $null
    }
}

function Ensure-Property($object, [string]$name, $default = "") {
    if ($null -eq $object.PSObject.Properties[$name]) {
        $object | Add-Member -NotePropertyName $name -NotePropertyValue $default
    }
}

function Write-ConfigFile([string]$path, $config) {
    $directory = Split-Path $path -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # Write beside the target and move it into place so a cancelled install can
    # never leave config.json torn or zero-filled.
    $json = $config | ConvertTo-Json -Depth 12
    $temp = Join-Path $directory (".config.json." + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temp -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temp) {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        }
    }
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

# What the setup .exe reports to the operator. The wizard runs this script hidden,
# so without a status file a till left without a printer, or without a service, still
# finishes with a green progress bar and nothing to show for it.
$printerState = "skipped"
$installFailures = New-Object System.Collections.Generic.List[string]
$statusFile = Join-Path $defaultRoot "install-status.txt"

function Write-InstallStatus([string]$state) {
    try {
        if (-not (Test-Path $defaultRoot)) {
            New-Item -ItemType Directory -Path $defaultRoot -Force | Out-Null
        }
        $lines = @(
            "state=$state",
            "printer=$printerState",
            "port=$Port",
            "runMode=$RunMode",
            "time=$((Get-Date).ToString('o'))"
        )
        foreach ($failure in $installFailures) {
            $lines += "failure=$failure"
        }
        Set-Content -Path $statusFile -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
    }
    catch {
        # Nothing left to do about a status file that will not write; the install
        # result is already on the console for anyone watching.
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

trap {
    # A terminating error anywhere below leaves the till half-configured, so record
    # how far it got before the exit code reaches the wizard.
    $installFailures.Add($_.Exception.Message)
    $printerState = if ($printerState -eq "skipped") { "not-reached" } else { $printerState }
    Write-InstallStatus "failed"
    Write-Error "PrintInterceptor installation failed: $($_.Exception.Message)"
    exit 1
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

# Restrict the root before the first config, status or log file is written.
# Creating it with default permissions first would leave an inherited-ACL window
# on config.json and the secret store.
if (-not (Test-Path $defaultRoot)) {
    New-Item -ItemType Directory -Path $defaultRoot -Force | Out-Null
}
Set-RestrictedAcl $defaultRoot $aclUser

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
Ensure-Property $config "printing" $null
if ($null -eq $config.printing) {
    $config.printing = [pscustomobject]@{}
}
Ensure-Property $config.printing "printerName"
Ensure-Property $config.printing "a4PrinterName"

# An existing config may already have explicit nulls from an earlier setup.
# The app normalises them on load, but do not keep writing them either.
foreach ($name in @("apiEndpoint", "apiKey", "ghostscriptPath")) {
    if ($null -eq $config.$name) { $config.$name = "" }
}
foreach ($name in @("printerName", "a4PrinterName")) {
    if ($null -eq $config.printing.$name) { $config.printing.$name = "" }
}

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

# Config and data folders are restricted before the first byte of either is
# written, including any custom paths supplied on the command line.
Set-RestrictedAcl (Split-Path $ConfigPath -Parent) $aclUser
Set-RestrictedAcl $dataDirectory $aclUser
Write-Host "Restricted ACLs on the configuration and data folders."

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

# -ApiKey is kept only for compatibility. The preferred path is --register-key,
# which reads the key from a hidden prompt and stores it with DPAPI + ACLs.
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Warning "-ApiKey writes the key to the machine environment, which any local user can read from the registry. Register it with '$exe --register-key' (hidden prompt) or Settings, then remove the machine variable."
    [Environment]::SetEnvironmentVariable("PRINTINTERCEPTOR_API_KEY", $ApiKey.Trim(), "Machine")
    Write-Host "Stored the API key in the machine environment (PRINTINTERCEPTOR_API_KEY)."
}

# 2. Virtual printer. This is the step the till cannot capture without, so it is
#    verified rather than assumed, and it never takes the rest of the install down
#    with it: a printer-less till still needs its service or its tray autostart.
if ($SkipPrinter) {
    Write-Host "Skipping the virtual printer."
    $printerState = "skipped"
}
else {
    $addPrinter = Resolve-SiblingScript "addprinter.ps1"
    try {
        & $addPrinter -PrinterName $PrinterName -HostAddress $HostAddress -Port $Port -Mode $PrinterMode
        if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
            $printerState = "installed"
        }
        else {
            throw "the printer is not in the Windows printer list afterwards."
        }
    }
    catch {
        $printerState = "failed"
        $repair = "& `"$addPrinter`" -PrinterName `"$PrinterName`" -HostAddress $HostAddress -Port $Port -Mode $PrinterMode"
        $installFailures.Add("Virtual printer '$PrinterName': $($_.Exception.Message)")
        Write-Warning "Virtual printer '$PrinterName' was not installed: $($_.Exception.Message)"
        Write-Warning "No print job will reach PrintInterceptor until this is fixed. Repair from an elevated prompt: $repair"
    }
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
Write-Host "  Virtual printer : $($printerState)$(if ($printerState -eq 'failed') { " - $PrinterName" })"
Write-Host "  Listen port     : $Port"
Write-Host "  Configuration   : $ConfigPath"
Write-Host "  Data and logs   : $dataDirectory"
Write-Host "Next: register the API key with '$exe --register-key' (hidden prompt) or open Settings in the tray, then restart."
Write-Host "Open the tray menu and choose 'Fiscal status…' to verify the device and fiscal day."

if ($installFailures.Count -gt 0) {
    Write-InstallStatus "partial"
    Write-Host ""
    Write-Error ("The install finished with problems:" + [Environment]::NewLine +
        ($installFailures -join [Environment]::NewLine))
    exit 1
}

Write-InstallStatus "ok"
exit 0
