# Registers the privileged scheduled task that runs auto-update.ps1 on each till.
# Run once per machine after the application is installed. The thumbprint pins
# the Authenticode certificate; it is not a secret and can be audited in the task.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SignerThumbprint,
    [string]$ManifestUrl = "https://updates.fiscalstack.co.zw/printinterceptor/update.json",
    [string]$Channel = "stable",
    [string]$ServiceName = "PrintInterceptor",
    [string]$AppDirectory = "",
    [string]$ConfigPath = "",
    [ValidateRange(5, 1440)]
    [int]$IntervalMinutes = 60,
    [string]$TaskName = "FiscalStack PrintInterceptor Updater",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed scheduled task '$TaskName'."
    exit 0
}

$updateScript = Join-Path $PSScriptRoot "auto-update.ps1"
if (-not (Test-Path $updateScript)) {
    throw "Updater script not found: $updateScript"
}

$arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -File `"$updateScript`" " +
             "-ManifestUrl `"$ManifestUrl`" -SignerThumbprint $SignerThumbprint " +
             "-Channel $Channel -ServiceName $ServiceName"
if (-not [string]::IsNullOrWhiteSpace($AppDirectory)) {
    $arguments += " -AppDirectory `"$AppDirectory`""
}
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $arguments += " -ConfigPath `"$ConfigPath`""
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
$start = (Get-Date).AddMinutes(10)
$trigger = New-ScheduledTaskTrigger -Once -At $start `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Scheduled task '$TaskName' will check '$ManifestUrl' every $IntervalMinutes minutes as SYSTEM."
Write-Host "Remove it with: .\\install-update-task.ps1 -Remove -TaskName '$TaskName'"
