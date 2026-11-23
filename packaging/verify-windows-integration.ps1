# End-to-end Windows validation for the paths that cannot run on a Linux or
# non-elevated dev host: release build + signing, bundled Ghostscript execution,
# silent Inno install, Windows service start, capture listener and status probe.
#
# Run from an elevated PowerShell on a Windows 10/11 or Server 2019+ agent:
#   .\packaging\verify-windows-integration.ps1 -PurgeOnCleanup
#
# The script creates a temporary self-signed code-signing certificate unless a
# real -Thumbprint is supplied, trusts it only for the duration of the run,
# installs the product, verifies it, then removes the service and test state.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$Version = "1.0.0",
    [string]$OutputRoot = "",
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "",
    [string]$ServiceName = "PrintInterceptor",
    [switch]$SkipSigning,
    [switch]$PurgeOnCleanup,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message =="
}

function Remove-TestService {
    param([string]$Name)

    $removeScript = Join-Path $RepoRoot "install-service.ps1"
    if ((Get-Service -Name $Name -ErrorAction SilentlyContinue) -and (Test-Path $removeScript)) {
        try { & $removeScript -Remove -ServiceName $Name -ErrorAction SilentlyContinue | Out-Null }
        catch { }
    }
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Service -Name $Name -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}

function Get-InstallDirectory {
    $uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{0F5D3A47-8C1E-4E49-9C54-4A4D8F3E6B21}_is1"
    if (Test-Path $uninstallKey) {
        $location = (Get-ItemProperty -Path $uninstallKey -Name "InstallLocation" -ErrorAction SilentlyContinue).InstallLocation
        if (-not [string]::IsNullOrWhiteSpace($location)) { return $location }
    }
    return (Join-Path ${env:ProgramFiles} "FiscalStack\PrintInterceptor")
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path $PSScriptRoot -Parent
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
$installScript = Join-Path $RepoRoot "packaging\install.ps1"
$buildScript = Join-Path $RepoRoot "packaging\build-release.ps1"

if (-not (Test-Path $buildScript)) { throw "Build script not found: $buildScript" }
if (-not (Test-Path $installScript)) { throw "Install script not found: $installScript" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "dist"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$publishDir = Join-Path $RepoRoot "publish"
$setupPath = Join-Path $OutputRoot "PrintInterceptor-$Version-setup.exe"
$createdCertificate = $false
$temporaryFiles = @()

try {
    Write-Step "Preparing signing certificate"
    if (-not $SkipSigning -and [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert `
            -Subject "CN=FiscalStack Windows Integration Test" `
            -CertStoreLocation "Cert:\CurrentUser\My" -ErrorAction Stop
        $Thumbprint = $cert.Thumbprint
        $createdCertificate = $true

        $cerPath = Join-Path $env:TEMP "fiscalstack-integration-$($Thumbprint).cer"
        Export-Certificate -Cert $cert -FilePath $cerPath -ErrorAction Stop | Out-Null
        $temporaryFiles += $cerPath

        Import-Certificate -FilePath $cerPath `
            -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" -Confirm:$false | Out-Null
        Import-Certificate -FilePath $cerPath `
            -CertStoreLocation "Cert:\LocalMachine\Root" -Confirm:$false | Out-Null
        Write-Host "Created and trusted temporary code-signing certificate $Thumbprint."
    }

    Write-Step "Removing any previous test service"
    Remove-TestService $ServiceName

    Write-Step "Building Windows release $Version"
    # Hashtable splatting is essential here: an array of "-Name", "value"
    # strings is passed positionally by PowerShell 5.1, which made -Thumbprint
    # land on build-release.ps1's -PfxPassword and fail before the build began.
    $buildParameters = @{ Version = $Version; OutputRoot = $OutputRoot; TimestampUrl = $TimestampUrl }
    if (-not $SkipSigning) { $buildParameters["Thumbprint"] = $Thumbprint }
    & $buildScript @buildParameters
    if (-not (Test-Path $setupPath)) {
        throw "Setup was not produced at $setupPath. Is Inno Setup installed?"
    }
    $signingDescription = if ($SkipSigning) { "unsigned" } else { "signed by $Thumbprint" }
    Write-Host "Release built ($signingDescription): $setupPath"

    Write-Step "Verifying the release manifest"
    $manifestPath = Join-Path $OutputRoot "update.json"
    if (-not (Test-Path $manifestPath)) { throw "Update manifest was not produced: $manifestPath" }
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $actualHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$manifest.sha256 -ne $actualHash) {
        throw "Manifest hash does not match the setup file: $($manifest.sha256) != $actualHash"
    }
    if (-not $SkipSigning) {
        $signature = Get-AuthenticodeSignature -FilePath $setupPath
        if ($signature.Status -ne "Valid") {
            throw "Setup signature is $($signature.Status): $($signature.StatusMessage)"
        }
        $signatureThumbprint = ([string]$signature.SignerCertificate.Thumbprint).Replace(" ", "").ToUpperInvariant()
        if ($signatureThumbprint -ne $Thumbprint.ToUpperInvariant()) {
            throw "Setup was signed by $signatureThumbprint, expected $Thumbprint."
        }
        Write-Host "Authenticode signature valid for $signatureThumbprint."
    }

    Write-Step "Executing bundled Ghostscript"
    $gswin = Join-Path $publishDir "ghostscript\bin\gswin64c.exe"
    if (-not (Test-Path $gswin)) {
        throw "Bundled Ghostscript not found at $gswin. Install 64-bit Ghostscript on this agent before running the build."
    }
    $postScript = Join-Path $env:TEMP "printinterceptor-gs-smoke.ps"
    $textOutput = Join-Path $env:TEMP "printinterceptor-gs-smoke.txt"
    $temporaryFiles += $postScript
    $temporaryFiles += $textOutput
    Set-Content -Path $postScript -Encoding ASCII -Value @(
        "%!PS",
        "/Times-Roman findfont 14 scalefont setfont",
        "72 720 moveto",
        "(FiscalStack CI smoke test) show",
        "showpage"
    )
    if (Test-Path $textOutput) { Remove-Item $textOutput -Force }
    & $gswin -q -dNOPAUSE -dBATCH -dSAFER "-sDEVICE=txtwrite" "-sOutputFile=$textOutput" $postScript
    if ($LASTEXITCODE -ne 0) { throw "Ghostscript txtwrite failed with exit code $LASTEXITCODE." }
    $extracted = Get-Content -Path $textOutput -Raw
    if ($extracted -notmatch "FiscalStack CI smoke test") {
        throw "Ghostscript ran but txtwrite did not contain the expected text: $extracted"
    }
    Write-Host "Bundled Ghostscript produced the expected txtwrite output."

    Write-Step "Running the silent installer"
    $setupLog = Join-Path $env:TEMP "printinterceptor-setup-integration.log"
    $temporaryFiles += $setupLog
    if (Test-Path $setupLog) { Remove-Item $setupLog -Force }
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=`"ghostscript`" /LOG=`"$setupLog`""
    $installer = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($installer.ExitCode -ne 0) {
        throw "Silent installer exited with $($installer.ExitCode). Setup log: $setupLog"
    }
    Write-Host "Silent installer completed."

    # Setup stays silent even when its [Run] step fails: install.ps1 records the
    # real result in this file, so surface it before waiting for a service that
    # was never created.
    $installStatusPath = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor\install-status.txt"
    if (-not (Test-Path $installStatusPath)) {
        throw "install.ps1 left no status at $installStatusPath. Setup log: $setupLog"
    }
    $installStatus = Get-Content -Path $installStatusPath -Raw
    if ($installStatus -notmatch "(?m)^state=ok\r?$") {
        throw "install.ps1 did not complete:$([Environment]::NewLine)$installStatus$([Environment]::NewLine)Setup log: $setupLog"
    }
    Write-Host "Install configuration completed ($($installStatus.Trim()))."

    Write-Step "Verifying the Windows service and capture listener"
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 1
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    } while (($null -eq $service -or $service.Status -ne "Running") -and (Get-Date) -lt $deadline)

    if ($null -eq $service) { throw "Service '$ServiceName' was not created." }
    if ($service.Status -ne "Running") { throw "Service '$ServiceName' is $($service.Status), not Running." }

    $installDirectory = Get-InstallDirectory
    $installedExe = Join-Path $installDirectory "PrintInterceptor.exe"
    if (-not (Test-Path $installedExe)) { throw "Installed executable not found: $installedExe" }
    # PrintInterceptor is a WinExe, so `&` from PowerShell does not wait for it
    # or reliably capture stdout. Redirect explicitly and wait for the process.
    $statusOut = Join-Path $env:TEMP "printinterceptor-status-$Version.out"
    $statusErr = Join-Path $env:TEMP "printinterceptor-status-$Version.err"
    $temporaryFiles += $statusOut
    $temporaryFiles += $statusErr
    $statusProcess = Start-Process -FilePath $installedExe `
        -ArgumentList "--status --json --service-name=`"$ServiceName`"" `
        -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $statusOut -RedirectStandardError $statusErr
    # Start-Process creates both redirect files even when a stream is empty;
    # Get-Content -Raw returns $null for a 0-byte file, so trim defensively.
    $statusRaw = if (Test-Path $statusOut) { Get-Content -Path $statusOut -Raw } else { "" }
    $statusErrorRaw = if (Test-Path $statusErr) { Get-Content -Path $statusErr -Raw } else { "" }
    $statusText = if ($null -eq $statusRaw) { "" } else { $statusRaw.Trim() }
    $statusErrorText = if ($null -eq $statusErrorRaw) { "" } else { $statusErrorRaw.Trim() }
    if ($statusProcess.ExitCode -ne 0) {
        throw "Status probe failed (exit $($statusProcess.ExitCode)): $statusText $statusErrorText"
    }
    if ([string]::IsNullOrWhiteSpace($statusText)) {
        throw "Status probe produced no output (exit $($statusProcess.ExitCode)): $statusErrorText"
    }
    $status = $statusText | ConvertFrom-Json
    if (-not $status.serviceInstalled -or [string]$status.serviceState -ne "RUNNING") {
        throw "Status probe did not see a running service. Raw output: $statusText"
    }
    if (-not $status.listenerReady) {
        throw "Status probe did not see the capture listener. Raw output: $statusText"
    }
    Write-Host "Service is Running and the capture listener is ready on the configured port."

    Write-Step "Verifying the installed Ghostscript component"
    $installedGhostscript = Join-Path $installDirectory "ghostscript\bin\gswin64c.exe"
    if (-not (Test-Path $installedGhostscript)) {
        throw "The ghostscript installer task was selected, but $installedGhostscript is missing."
    }
    $configPath = [string]$status.configPath
    if ([string]::IsNullOrWhiteSpace($configPath)) {
        $configPath = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor\config.json"
    }
    $installedConfig = Get-Content -Path $configPath -Raw | ConvertFrom-Json
    $configuredGhostscript = [string]$installedConfig.ghostscriptPath
    if ([string]::IsNullOrWhiteSpace($configuredGhostscript) -or -not (Test-Path $configuredGhostscript)) {
        throw "The installer did not configure a working ghostscriptPath (got '$configuredGhostscript')."
    }
    $installedTextOutput = Join-Path $env:TEMP "printinterceptor-gs-installed.txt"
    $temporaryFiles += $installedTextOutput
    if (Test-Path $installedTextOutput) { Remove-Item $installedTextOutput -Force }
    & $installedGhostscript -q -dNOPAUSE -dBATCH -dSAFER "-sDEVICE=txtwrite" `
        "-sOutputFile=$installedTextOutput" $postScript
    if ($LASTEXITCODE -ne 0) { throw "Installed Ghostscript failed with exit code $LASTEXITCODE." }
    if ((Get-Content -Path $installedTextOutput -Raw) -notmatch "FiscalStack CI smoke test") {
        throw "Installed Ghostscript ran but did not produce the expected txtwrite output."
    }
    Write-Host "Installed Ghostscript component is present, configured and working."

    Write-Host ""
    Write-Host "WINDOWS INTEGRATION VALIDATION PASSED"
}
finally {
    Write-Step "Cleaning up test state"
    Remove-TestService $ServiceName
    $uninstallParameters = @{ Uninstall = $true; ServiceName = $ServiceName; SkipPrinter = $true }
    if ($PurgeOnCleanup) { $uninstallParameters["Purge"] = $true }
    try { & $installScript @uninstallParameters -ErrorAction SilentlyContinue | Out-Null }
    catch { }

    if ($createdCertificate -and -not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        foreach ($store in @("Cert:\CurrentUser\My", "Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher")) {
            $certPath = Join-Path $store $Thumbprint
            if (Test-Path $certPath) { Remove-Item $certPath -Force -ErrorAction SilentlyContinue }
        }
    }

    if (-not $KeepArtifacts) {
        foreach ($file in $temporaryFiles) {
            if (Test-Path $file) { Remove-Item $file -Force -ErrorAction SilentlyContinue }
        }
    }
}
