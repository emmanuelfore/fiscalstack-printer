# Builds a release of PrintInterceptor: self-contained win-x64 publish, optional
# Authenticode signing, a zip and (when Inno Setup is installed) a setup .exe.
#
# Run on a Windows build agent. Nothing here can run on the Linux dev host, so
# the signing and installer steps are verified on Windows before shipping.
#
# Signing: prefer -Thumbprint with a certificate already in the Windows user or
# machine store (including an HSM CSP/KSP). A PFX is imported into the current
# user store first with its password as a SecureString; no password is ever
# converted to plain text or placed on a signtool / ISCC command line.
#
# Examples:
#   .\packaging\build-release.ps1 -Version 1.2.0
#   .\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123456789ABCDEF...
#   .\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx
#   .\packaging\build-release.ps1 -Version 1.2.0 -GhostscriptDir "C:\Program Files\gs\gs10.08.0"
#   .\packaging\build-release.ps1 -Version 1.2.0 -NoGhostscript
#   .\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123... `
#       -Channel stable -UpdateBaseUrl https://updates.example.com/printinterceptor

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$PfxPath = "",
    [SecureString]$PfxPassword,
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "https://timestamp.digicert.com",
    [switch]$SkipInstaller,
    [string]$InnoCompiler = "",
    [string]$GhostscriptDir = "",
    [switch]$NoGhostscript,
    [switch]$StripGhostscriptFonts,
    [string]$SourceOfferContact = "",
    [string]$Channel = "stable",
    [string]$UpdateBaseUrl = ""
)

$ErrorActionPreference = "Stop"

# Tracks a certificate imported from a PFX for this build only. Thumbprint
# signing never needs a password on a command line, and the temporary import is
# removed even when a later step fails.
$importedSigningThumbprint = $null

function Remove-ImportedSigningCertificate {
    if ([string]::IsNullOrWhiteSpace($importedSigningThumbprint)) { return }
    try {
        $path = "Cert:\CurrentUser\My\$importedSigningThumbprint"
        if (Test-Path $path) {
            Remove-Item -Path $path -ErrorAction Stop
            Write-Host "Removed the temporary signing certificate from Cert:\CurrentUser\My."
        }
    }
    catch {
        Write-Warning "Could not remove the temporary signing certificate from Cert:\CurrentUser\My: $($_.Exception.Message)"
    }
}

trap { Remove-ImportedSigningCertificate; throw }

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Date -Format "yyyy.MM.dd") + "-dev"
}

$publishDir = Join-Path $repoRoot "publish"
$stageDir = Join-Path $OutputRoot "PrintInterceptor-$Version-$Runtime"
$exe = Join-Path $publishDir "PrintInterceptor.exe"
$setupName = "PrintInterceptor-$Version-setup"

Write-Host "Building PrintInterceptor $Version ($Runtime)."
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# AssemblyVersion/FileVersion must be numeric; the informational version may
# carry a suffix (e.g. 2026.10.31-dev).
$numericVersion = ($Version -split "-")[0]

& dotnet publish (Join-Path $repoRoot "src\PrintInterceptor\PrintInterceptor.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion=$numericVersion `
    -p:FileVersion=$numericVersion `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# Ship the install helpers next to the executable so the packaged setup and the
# standalone zip can both provision the printer and service.
foreach ($script in @(
    (Join-Path $repoRoot "addprinter.ps1"),
    (Join-Path $repoRoot "install-service.ps1"),
    (Join-Path $PSScriptRoot "install.ps1")
)) {
    Copy-Item -Path $script -Destination $publishDir -Force
}

# The updater lives in its own folder so the setup package ships it at
# {app}\updater. It is installed as a SYSTEM scheduled task separately; a
# release must not silently change a till's update policy.
$updaterStage = Join-Path $publishDir "updater"
if (-not (Test-Path $updaterStage)) { New-Item -ItemType Directory -Path $updaterStage -Force | Out-Null }
foreach ($script in @("auto-update.ps1", "install-update-task.ps1", "rollback.ps1")) {
    Copy-Item -Path (Join-Path $PSScriptRoot $script) -Destination (Join-Path $updaterStage $script) -Force
}

# Ghostscript is needed when the till captures PostScript or PDF jobs, so a
# release bundles it by default: staging it under publish\ghostscript puts it in
# both the zip and the setup .exe (which ship publish\* recursively), and a till
# then needs no separate install. The source is whatever the build host already
# has — the folder named by -GhostscriptDir, gswin64c.exe on PATH, or the newest
# Program Files\gs\gs*. It is never downloaded: a release build pulling a
# third-party binary off the network is a supply-chain risk, so Ghostscript is
# installed from the official artifact first and this script only copies it.
#
# Bundling conveys Ghostscript's binaries under AGPL-3.0, which obliges whoever
# ships the package to carry the licence text and the corresponding source, or to
# hold a commercial licence from Artifex. The script copies any licence file
# beside the binaries and writes SOURCE-OFFER.txt naming the exact version
# bundled; -SourceOfferContact is the address recipients write to. That file
# records a written offer, it does not make one: section 6 of AGPL-3.0 wants the
# offer honoured for at least three years from the last distribution, which no
# build script can sign for the distributor. -NoGhostscript skips the bundle,
# which is the choice for a Generic / Text Only deployment.
function ConvertTo-GhostscriptVersion {
    param([string]$FolderName)

    # gs10.08.0 -> 10.08.0. Anything the version cast rejects (rc tags, hand-named
    # folders) compares as 0.0 so a recognisable install always wins.
    $digits = ($FolderName -replace "^gs", "") -replace "[^0-9\.]+.*$", ""
    try {
        return [Version]$digits
    }
    catch {
        return (New-Object Version -ArgumentList 0, 0)
    }
}

function Resolve-GhostscriptInstall {
    param([string]$ExplicitDir)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitDir)) {
        if (-not (Test-Path $ExplicitDir)) {
            throw "GhostscriptDir '$ExplicitDir' does not exist. Point it at the folder containing bin\gswin64c.exe."
        }
        return (Resolve-Path $ExplicitDir).Path
    }

    $onPath = Get-Command "gswin64c.exe" -ErrorAction SilentlyContinue
    if ($onPath) {
        # bin\gswin64c.exe sits one level under the Ghostscript folder, and a real
        # install always carries Resource next to bin.
        $binDir = Split-Path $onPath.Source -Parent
        $folder = Split-Path $binDir -Parent
        if (Test-Path (Join-Path $folder "Resource")) { return $folder }
    }

    foreach ($root in @("${env:ProgramFiles}\gs", "${env:ProgramFiles(x86)}\gs")) {
        if (-not (Test-Path $root)) { continue }

        # "Newest" means newest by the version in the folder name. As text,
        # gs9.53.3 sorts above gs10.08.0, and that order would bundle an old,
        # unpatched Ghostscript into every till.
        $found = Get-ChildItem -Path $root -Directory -Filter "gs*" -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "bin\gswin64c.exe") } |
            Sort-Object { ConvertTo-GhostscriptVersion $_.Name } -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

if ($NoGhostscript) {
    Write-Host "Skipped the Ghostscript bundle (-NoGhostscript): a till that captures PostScript or PDF jobs will need 64-bit Ghostscript installed, or the Generic / Text Only driver instead."
}
else {
    $gsSource = Resolve-GhostscriptInstall -ExplicitDir $GhostscriptDir
    if (-not $gsSource) {
        throw "No 64-bit Ghostscript to bundle. Looked for gswin64c.exe on PATH and under Program Files\gs. Install the official release on this build host, name the folder with -GhostscriptDir, or ship without it using -NoGhostscript."
    }

    if (-not (Test-Path (Join-Path $gsSource "bin\gswin64c.exe"))) {
        throw "No bin\gswin64c.exe under $gsSource; that is not a 64-bit Ghostscript folder."
    }

    $gsTarget = Join-Path $publishDir "ghostscript"
    if (Test-Path $gsTarget) { Remove-Item $gsTarget -Recurse -Force }
    New-Item -ItemType Directory -Path $gsTarget -Force | Out-Null

    # Ghostscript finds its own resources relative to the executable, so bin, lib
    # and Resource have to travel together; doc and the installer icons need not.
    foreach ($folder in @("bin", "lib", "Resource")) {
        $source = Join-Path $gsSource $folder
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination (Join-Path $gsTarget $folder) -Recurse -Force
        }
    }

    # Ship the licence text that covers the redistributed binaries. Official
    # Ghostscript installs keep the AGPL text in doc\COPYING rather than beside
    # the executables, so look there too before warning the release manager.
    $licenceFiles = @(Get-ChildItem -Path $gsSource -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^(license|copying|agpl|gpl|notice)" })
    if ($licenceFiles.Count -eq 0) {
        $licenceFiles = @(Get-ChildItem -Path (Join-Path $gsSource "doc") -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^(license|copying|agpl|gpl|notice)" })
    }
    if ($licenceFiles.Count -eq 0) {
        Write-Warning "No licence file beside the Ghostscript binaries in $gsSource. SOURCE-OFFER.txt says so; add the AGPL text to the package before shipping it."
    }
    else {
        $licenceFiles | Copy-Item -Destination $gsTarget -Force
    }

    if ($StripGhostscriptFonts) {
        $fontDir = Join-Path $gsTarget "Resource\Font"
        if (Test-Path $fontDir) {
            Remove-Item $fontDir -Recurse -Force
            Write-Host "Stripped Resource\Font: a much smaller bundle, but glyph substitutes may appear on printed invoices."
        }
    }

    # What version is actually bundled, for the source offer. The executable's
    # own version info is authoritative; the folder name is the fallback.
    $gsVersionInfo = (Get-Item (Join-Path $gsTarget "bin\gswin64c.exe")).VersionInfo
    $gsVersion = $gsVersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($gsVersion)) { $gsVersion = $gsVersionInfo.FileVersion }
    if ([string]::IsNullOrWhiteSpace($gsVersion)) { $gsVersion = Split-Path $gsSource -Leaf }

    $licenceNames = ($licenceFiles | ForEach-Object { $_.Name }) -join ", "
    if ([string]::IsNullOrWhiteSpace($licenceNames)) { $licenceNames = "none found next to the binaries - ADD THE AGPL-3.0 TEXT TO THIS FOLDER" }

    $contact = $SourceOfferContact
    if ([string]::IsNullOrWhiteSpace($contact)) {
        $contact = "NOT SET - this build ran without -SourceOfferContact. Put the postal or e-mail address a recipient can use HERE, BEFORE shipping this package: section 6 of AGPL-3.0 puts this obligation on whoever distributes it, and this file only records it."
    }

    $sourceOffer = @"
Ghostscript bundled with PrintInterceptor
=========================================

Bundled version : $gsVersion
Bundled from    : $gsSource
Bundled on      : $((Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm")) UTC
Licence         : GNU Affero General Public License version 3 (AGPL-3.0)
Licence text    : $licenceNames

Corresponding source
--------------------
The binaries in this folder are the object code of the Ghostscript release named
above. Its complete corresponding source is the matching source tarball that
Artifex publishes with every release; see the Ghostscript releases page linked
from https://www.ghostscript.com/

Written offer (AGPL-3.0 section 6)
----------------------------------
The distributor of this package offers any recipient of it the complete
corresponding source code of the Ghostscript version bundled here, on a physical
or network medium, for a charge no more than the cost of performing the
distribution, for at least three years from the last distribution of this
package. Requests go to the address below.

Who to ask       : $contact

Two things this file does not do
--------------------------------
- It does not grant a licence. These binaries arrive under AGPL-3.0. The
  alternative to meeting its conditions is a commercial licence from Artifex, the
  Ghostscript copyright holder.
- It does not make the offer. This text is generated by
  packaging\build-release.ps1; a written offer binds whoever distributes the
  package, and it is only meaningful if the contact above reaches someone.
"@
    Set-Content -Path (Join-Path $gsTarget "SOURCE-OFFER.txt") -Value $sourceOffer -Encoding UTF8

    $gsSizeMb = [math]::Round(
        (Get-ChildItem -Path $gsTarget -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "Bundled Ghostscript $gsVersion from $gsSource ($gsSizeMb MB) into $gsTarget."
    Write-Host "Licence text: $licenceNames"
    Write-Host "Source offer: $(Join-Path $gsTarget 'SOURCE-OFFER.txt')"
}

function Resolve-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem -Path $root -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

$signTool = $null
$signingThumbprint = $Thumbprint.Trim()
if (-not [string]::IsNullOrWhiteSpace($PfxPath) -or -not [string]::IsNullOrWhiteSpace($signingThumbprint)) {
    $signTool = Resolve-SignTool
    if (-not $signTool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK or add signtool to PATH."
    }
}

if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    if (-not [string]::IsNullOrWhiteSpace($signingThumbprint)) {
        throw "Pass either -PfxPath or -Thumbprint, not both."
    }

    # Import with the password still a SecureString, then sign by thumbprint.
    # signtool and ISCC only ever see a /sha1 value, never /p or /f.
    $existingThumbprints = @(Get-ChildItem "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Thumbprint })
    $pfxFilePath = (Resolve-Path $PfxPath).Path
    $importArgs = @{ FilePath = $pfxFilePath; CertStoreLocation = "Cert:\CurrentUser\My" }
    if ($null -ne $PfxPassword) { $importArgs["Password"] = $PfxPassword }

    $imported = Import-PfxCertificate @importArgs -ErrorAction Stop
    $signingThumbprint = $imported.Thumbprint
    if ($existingThumbprints -notcontains $signingThumbprint) {
        $importedSigningThumbprint = $signingThumbprint
    }
    Write-Host "Imported the signing certificate into Cert:\CurrentUser\My ($signingThumbprint) for this build. The PFX password was not placed on a command line."
}

if ($signTool) {
    if ([string]::IsNullOrWhiteSpace($signingThumbprint)) {
        throw "Signing was requested without a certificate. Pass -Thumbprint or -PfxPath."
    }
    $cert = Get-Item "Cert:\CurrentUser\My\$signingThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { $cert = Get-Item "Cert:\LocalMachine\My\$signingThumbprint" -ErrorAction SilentlyContinue }
    if (-not $cert) {
        throw "No certificate with thumbprint $signingThumbprint was found in the CurrentUser or LocalMachine My store."
    }
}

function Get-SignArguments {
    $arguments = @("sign", "/fd", "SHA256")
    # An empty -TimestampUrl is used by the integration verifier so a temporary
    # self-signed certificate does not need network access. Production builds
    # keep the default RFC3161 timestamp.
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/td", "SHA256", "/tr", $TimestampUrl)
    }
    $arguments += @("/sha1", $signingThumbprint)
    return $arguments
}

function Sign-File([string]$path) {
    if (-not $signTool) { return }

    $arguments = (Get-SignArguments) + $path
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $path ($LASTEXITCODE)." }
    Write-Host "Signed $(Split-Path $path -Leaf)."
}

if ($signTool) {
    Get-ChildItem -Path $publishDir -Filter "PrintInterceptor*.exe" | ForEach-Object { Sign-File $_.FullName }
}

# Zip for manual installs and USB distribution.
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force

$zipPath = Join-Path $OutputRoot "PrintInterceptor-$Version-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Zip: $zipPath"

# Setup .exe through Inno Setup when it is available.
if (-not $SkipInstaller) {
    if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
        $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    }

    if (-not (Test-Path $InnoCompiler)) {
        Write-Warning "Inno Setup was not found at $InnoCompiler; skipped the setup .exe. Use -SkipInstaller to silence this."
    }
    else {
        $iss = Join-Path $PSScriptRoot "PrintInterceptor.iss"

        # ISCC is a Delphi program: build the whole command line as a string and
        # let Start-Process pass it through untouched (PowerShell 5.1 mangles
        # embedded quotes when splatting an array to a native program). Inside
        # /Ssigntool, Inno's own $q constant supplies a literal quote.
        $isccArguments = "/DAppVersion=$Version /O`"$OutputRoot`""

        # Only expose the Ghostscript component when this build actually bundled
        # it; otherwise ISCC would compile a Files entry with no matching source.
        if (Test-Path (Join-Path $publishDir "ghostscript\bin\gswin64c.exe")) {
            $isccArguments += " /DGhostscriptBundled=1"
        }

        # The installer signs itself and the uninstaller through Inno's SignTool
        # directive. Thumbprint only: no PFX path, no password on any command line.
        if ($signTool) {
            $isccArguments += " /DSignToolConfigured=1"

            # Inno uses $q (not "") for a quote inside a /S command, and $f for
            # the file being signed. Both must survive PowerShell interpolation.
            $innoSign = '$q' + $signTool + '$q sign /fd SHA256'
            if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
                $innoSign += ' /td SHA256 /tr $q' + $TimestampUrl + '$q'
            }
            $innoSign += ' /sha1 ' + $signingThumbprint

            $isccArguments += " `"/Ssigntool=$innoSign `$f`""
        }

        $isccArguments += " `"$iss`""

        $iscc = Start-Process -FilePath $InnoCompiler -ArgumentList $isccArguments -Wait -PassThru -NoNewWindow
        if ($iscc.ExitCode -ne 0) { throw "Inno Setup failed ($($iscc.ExitCode))." }

        $setup = Join-Path $OutputRoot "$setupName.exe"
        if (Test-Path $setup) {
            # Inno signs the setup and uninstaller itself when /Ssigntool was supplied.
            Write-Host "Installer: $setup"
        }
    }
}

# Update manifest. The setup file may be hosted under a different public name,
# so -UpdateBaseUrl sets the prefix; a relative setupUrl is resolved against the
# manifest URL by the updater. The signer thumbprint is only present for signed
# builds, and auto-update.ps1 always pins its own expected thumbprint anyway.
$setupPath = Join-Path $OutputRoot "$setupName.exe"
if (-not $SkipInstaller -and (Test-Path $setupPath)) {
    if ([string]::IsNullOrWhiteSpace($UpdateBaseUrl)) {
        $setupUrl = "$setupName.exe"
    }
    else {
        $setupUrl = $UpdateBaseUrl.TrimEnd('/') + "/" + $setupName + ".exe"
    }

    $setupHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestThumbprint = ""
    if ($signTool) {
        $manifestThumbprint = ($signingThumbprint -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()
    }
    $ghostscriptBundled = Test-Path (Join-Path $publishDir "ghostscript\bin\gswin64c.exe")
    $installTasks = if ($ghostscriptBundled) { "printer,ghostscript" } else { "printer" }

    $manifest = [ordered]@{
        version          = $Version
        channel          = $Channel
        setupUrl         = $setupUrl
        sha256           = $setupHash
        signerThumbprint = $manifestThumbprint
        publishedAt      = (Get-Date).ToUniversalTime().ToString("o")
        installTasks     = $installTasks
    }

    $manifestPath = Join-Path $OutputRoot "update.json"
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8
    Copy-Item -Path $manifestPath -Destination (Join-Path $OutputRoot "update-$Version.json") -Force
    Write-Host "Update manifest: $manifestPath"
}

Remove-ImportedSigningCertificate

Write-Host ""
Write-Host "Release $Version complete:"
Write-Host "  Publish : $publishDir"
Write-Host "  Zip     : $zipPath"
if (-not $SkipInstaller -and (Test-Path $setupPath)) {
    Write-Host "  Setup   : $setupPath"
}
