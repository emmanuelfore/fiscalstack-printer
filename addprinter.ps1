# Installs the virtual printer that feeds PrintInterceptor.
# Requires administrator privileges.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$PrinterName = "FiscalStack Fiscal Printer",
    [string]$HostAddress = "127.0.0.1",
    [int]$Port = 9100,
    [ValidateSet("Text", "PostScript")]
    [string]$Mode = "Text",
    [switch]$Remove,
    [switch]$TestPage
)

$ErrorActionPreference = "Stop"
$portName = "FiscalStack_Raw_$Port"

if ($Remove) {
    $printer = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
    if ($printer) {
        Remove-Printer -Name $PrinterName
        Write-Host "Removed printer '$PrinterName'."
    }

    $port = Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue
    if ($port) {
        Remove-PrinterPort -Name $portName
        Write-Host "Removed port '$portName'."
    }

    exit 0
}

$driverName = if ($Mode -eq "PostScript") { "Microsoft PS Class Driver" } else { "Generic / Text Only" }

if (-not (Get-PrinterDriver -Name $driverName -ErrorAction SilentlyContinue)) {
    $available = (Get-PrinterDriver | Select-Object -ExpandProperty Name) -join ", "
    throw "Printer driver '$driverName' is not installed. Available drivers: $available"
}

if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
    Write-Host "Printer '$PrinterName' already exists."
}
else {
    if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
        Add-PrinterPort -Name $portName -PrinterHostAddress $HostAddress -PortNumber $Port
        Write-Host "Created raw TCP port '$portName' -> ${HostAddress}:$Port."
    }

    Add-Printer -Name $PrinterName -DriverName $driverName -PortName $portName
    Write-Host "Created printer '$PrinterName' with driver '$driverName'."
}

Write-Host ""
Write-Host "POS setup:"
Write-Host "  - Send receipts to '$PrinterName'."
Write-Host "  - Do NOT set it as the Windows default printer."
Write-Host "  - The fiscal receipt is printed by PrintInterceptor on the real till printer."
Write-Host ""

if ($Mode -eq "PostScript") {
    Write-Host "PostScript mode requires Ghostscript on this machine (set ghostscriptPath if it is not on PATH)."
}

if ($TestPage) {
    "FiscalStack PrintInterceptor test page" | Out-Printer -Name $PrinterName
    Write-Host "Test page sent. Check the service log and the fiscal printer."
}
