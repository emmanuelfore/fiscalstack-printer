# Installs PrintInterceptor as a Windows service.
# Run PowerShell as Administrator.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$ExePath = "",
    [string]$ConfigPath = "",
    [string]$ServiceName = "PrintInterceptor",
    [string]$DisplayName = "FiscalStack Print Interceptor",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

function Invoke-Sc([string]$arguments) {
    # Start-Process takes this string verbatim, so quoted values survive intact.
    $process = Start-Process -FilePath "sc.exe" -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    return $process.ExitCode
}

if ($Remove) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Invoke-Sc "delete `"$ServiceName`"" | Out-Null
    Write-Host "Service '$ServiceName' removed."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "publish\PrintInterceptor.exe"
}

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath. Publish first: dotnet publish src\PrintInterceptor -c Release -r win-x64 --self-contained true -o publish"
}

$exe = (Resolve-Path $ExePath).Path
$binPath = "`"$exe`""

if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $config = (Resolve-Path $ConfigPath).Path
    $binPath = "$binPath --config `"$config`""
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Remove it first with: .\install-service.ps1 -Remove"
}

# New-Service passes the binary path straight to the service manager, so the
# quotes around the exe and config paths are not re-parsed by a shell.
try {
    New-Service -Name $ServiceName `
        -BinaryPathName $binPath `
        -DisplayName $DisplayName `
        -StartupType Automatic | Out-Null
}
catch {
    throw "Could not create service '$ServiceName': $($_.Exception.Message)"
}

$description = "Captures POS print jobs from a virtual printer, fiscalises them and prints the ZIMRA fiscal receipt."
if ((Invoke-Sc "description `"$ServiceName`" `"$description`"") -ne 0) {
    Write-Warning "The service was created, but its description could not be set."
}

if ((Invoke-Sc "failure `"$ServiceName`" reset= 86400 actions= restart/60000/restart/60000/restart/60000") -ne 0) {
    Write-Warning "The service was created, but restart-on-failure recovery could not be configured."
}

try {
    Start-Service -Name $ServiceName
}
catch {
    throw "Service '$ServiceName' was created but did not start: $($_.Exception.Message). Check the log under %ProgramData%\FiscalStack\PrintInterceptor\data\logs."
}

Write-Host "Service '$ServiceName' installed and started."
Write-Host "Configuration: $(if ($ConfigPath) { $ConfigPath } else { '%ProgramData%\FiscalStack\PrintInterceptor\config.json' })"
Write-Host "Logs: %ProgramData%\FiscalStack\PrintInterceptor\data\logs"
