# Restores the application folder and the fiscal state captured by auto-update.ps1.
# Normally called automatically when an upgrade fails; it can also be run by an
# operator with the backup path printed by the updater.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,
    [string]$AppDirectory = "",
    [string]$ServiceName = "PrintInterceptor",
    [switch]$NoService
)

$ErrorActionPreference = "Stop"

function Write-UpdateEvent {
    param([string]$Message, [string]$Level = "Information", [int]$EventId = 703)

    try {
        if (-not (Get-EventLog -LogName Application -Source "PrintInterceptor" -ErrorAction SilentlyContinue)) {
            New-EventLog -LogName Application -Source "PrintInterceptor" -ErrorAction Stop | Out-Null
        }
        Write-EventLog -LogName Application -Source "PrintInterceptor" -EntryType $Level `
            -EventId $EventId -Message $Message -ErrorAction Stop
    }
    catch { }
}

function Invoke-RoboCopy {
    param([string]$Source, [string]$Destination, [switch]$Mirror)

    if (-not (Test-Path $Source)) { return }

    $arguments = @($Source, $Destination, "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")
    if ($Mirror) { $arguments += "/MIR" } else { $arguments += "/E" }

    & robocopy.exe @arguments | Out-Null
    $code = $LASTEXITCODE
    # robocopy uses 0-7 for success/informational outcomes; 8+ is a real failure.
    if ($code -ge 8) {
        throw "robocopy failed with exit code $code for $Source -> $Destination."
    }
}

$backupPath = (Resolve-Path $BackupPath).Path
$infoPath = Join-Path $backupPath "backup.json"
if (-not (Test-Path $infoPath)) {
    throw "Not an update backup: $infoPath is missing."
}

$info = Get-Content -Path $infoPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    $AppDirectory = [string]$info.appDirectory
}
if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    throw "No application directory in the backup and none was supplied with -AppDirectory."
}
if ([string]::IsNullOrWhiteSpace($ServiceName)) {
    $ServiceName = [string]$info.serviceName
}

$root = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor"
$dataDir = $root
if ($null -ne $info.dataDirectory -and -not [string]::IsNullOrWhiteSpace([string]$info.dataDirectory)) {
    $dataDir = [string]$info.dataDirectory
}
$backupApp = Join-Path $backupPath "app"
if (-not (Test-Path $backupApp)) {
    throw "The backup has no app folder: $backupApp"
}

$service = $null
if (-not $NoService) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne "Stopped" -and
               (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }
}

# /MIR here: the bad upgrade may have added files that must disappear. The state
# backup is overlayed with /E instead, so anything written during the update
# window is not deleted blindly.
Invoke-RoboCopy -Source $backupApp -Destination $AppDirectory -Mirror

$configPath = Join-Path $root "config.json"
if ($null -ne $info.configPath -and -not [string]::IsNullOrWhiteSpace([string]$info.configPath)) {
    $configPath = [string]$info.configPath
}
if (Test-Path (Join-Path $backupPath "config.json")) {
    Copy-Item -Path (Join-Path $backupPath "config.json") -Destination $configPath -Force
}
foreach ($folder in @("secrets", "profiles")) {
    Invoke-RoboCopy -Source (Join-Path $backupPath $folder) -Destination (Join-Path $root $folder)
}
Invoke-RoboCopy -Source (Join-Path $backupPath "data") -Destination $dataDir

$aclFile = Join-Path $backupPath "root-acl.txt"
if (Test-Path $aclFile) {
    & icacls.exe $root /restore $aclFile | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not restore ACLs from $aclFile (icacls exit $LASTEXITCODE)."
    }
}

if ($null -ne $service -and -not $NoService) {
    Start-Service -Name $ServiceName -ErrorAction Stop
}

Write-Host "Rolled back PrintInterceptor to $([string]$info.version) from $backupPath."
Write-UpdateEvent "PrintInterceptor rolled back to $([string]$info.version) from $backupPath."
