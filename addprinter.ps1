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

function Test-PrinterDriverInstalled([string]$name) {
    return [bool](Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue)
}

function Get-PrinterDriverFeature([string]$name) {
    # The optional Windows feature that carries an in-box driver, when one does.
    # "Generic / Text Only" is the usual absence: images that have never printed
    # from a text device do not have it staged.
    if ($name -eq "Generic / Text Only") { return "VirtualPrinter-printer-03-0" }
    return $null
}

function Install-PrinterDriverIfMissing([string]$name) {
    if (Test-PrinterDriverInstalled $name) { return $true }

    # A setup that only reports "driver not installed" leaves the till operator
    # hunting through Print Management, so try the two in-box routes first: the
    # driver store, then the optional feature that stages it. Both are wrapped
    # because the print cmdlets raise a hard error when the caller is not elevated
    # or the image has no copy, and either has to fall through to the next attempt
    # rather than end the install.
    Write-Host "Printer driver '$name' is not installed; adding the in-box copy."
    try {
        Add-PrinterDriver -Name $name -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Host "  In-box driver add failed: $($_.Exception.Message)"
    }
    if (Test-PrinterDriverInstalled $name) { return $true }

    $feature = Get-PrinterDriverFeature $name
    if ($feature) {
        Write-Host "Enabling the Windows optional feature that carries it: $feature."
        try {
            Enable-WindowsOptionalFeature -Online -FeatureName $feature -NoRestart -ErrorAction Stop |
                Out-Null
            Add-PrinterDriver -Name $name -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Host "  Optional feature route failed: $($_.Exception.Message)"
        }
        if (Test-PrinterDriverInstalled $name) { return $true }
    }

    return $false
}

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

if (-not (Install-PrinterDriverIfMissing $driverName)) {
    $available = (Get-PrinterDriver | Select-Object -ExpandProperty Name) -join ", "
    $otherMode = if ($Mode -eq "Text") { "PostScript" } else { "Text" }
    throw ("Printer driver '$driverName' is not installed and could not be added from the in-box copy. " +
        "Drivers on this machine: $available. Add the driver in Print Management, or capture in " +
        "$otherMode mode instead: addprinter.ps1 -PrinterName '$PrinterName' -Mode $otherMode.")
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

# Windows accepts an Add-Printer that never lands in the list when the spooler
# rejects the driver/port pair, and a till configured to print into a printer that
# is not there loses the sale quietly. Check the printer this script exists to
# create is actually visible before declaring success.
$created = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if (-not $created) {
    throw "The printer '$PrinterName' was not created: Windows does not list it. The spooler usually means the driver '$driverName' cannot bind to port '$portName'."
}
if ($created.PortName -ne $portName) {
    Write-Warning ("'$PrinterName' is bound to port '$($created.PortName)', not '$portName', so jobs " +
        "are not reaching ${HostAddress}:$Port. Reset it with: Set-Printer -Name '$PrinterName' -PortName '$portName'.")
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
