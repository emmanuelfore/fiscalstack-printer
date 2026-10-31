# Builds a release of PrintInterceptor: self-contained win-x64 publish, optional
# Authenticode signing, a zip and (when Inno Setup is installed) a setup .exe.
#
# Run on a Windows build agent. Nothing here can run on the Linux dev host, so
# the signing and installer steps are verified on Windows before shipping.
#
# Examples:
#   .\packaging\build-release.ps1 -Version 1.2.0
#   .\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx
#   .\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123456789ABCDEF...

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$PfxPath = "",
    [SecureString]$PfxPassword,
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipInstaller,
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"

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
$pfxFilePath = $null
$pfxPasswordPlain = $null
if (-not [string]::IsNullOrWhiteSpace($PfxPath) -or -not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    $signTool = Resolve-SignTool
    if (-not $signTool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK or add signtool to PATH."
    }
}

if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    $pfxFilePath = (Resolve-Path $PfxPath).Path
    if ($null -ne $PfxPassword) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
        try {
            $pfxPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function Get-SignArguments {
    $arguments = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampUrl)
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $arguments += @("/sha1", $Thumbprint)
    }
    else {
        $arguments += @("/f", $pfxFilePath)
        if ($pfxPasswordPlain) {
            $arguments += @("/p", $pfxPasswordPlain)
        }
    }

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

        # ISCC is a Delphi program: an argument is one quoted parameter and ""
        # inside it is a literal quote. Build the whole command line as a string
        # and let Start-Process pass it through untouched (PowerShell 5.1 mangles
        # embedded quotes when splatting an array to a native program).
        $isccArguments = "/DAppVersion=$Version /O`"$OutputRoot`""

        # The installer signs itself and the uninstaller with this command.
        if ($signTool) {
            $innoSign = "`"$signTool`" sign /fd SHA256 /td SHA256 /tr `"$TimestampUrl`""

            if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
                $innoSign += " /sha1 $Thumbprint"
            }
            else {
                $innoSign += " /f `"$pfxFilePath`""
                if ($pfxPasswordPlain) {
                    $innoSign += " /p `"$pfxPasswordPlain`""
                }
            }

            $isccArguments += " `"/Ssigntool=$($innoSign -replace '"', '""') `$f`""
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

Write-Host ""
Write-Host "Release $Version complete:"
Write-Host "  Publish : $publishDir"
Write-Host "  Zip     : $zipPath"
if (Test-Path (Join-Path $OutputRoot "$setupName.exe")) {
    Write-Host "  Setup   : $(Join-Path $OutputRoot "$setupName.exe")"
}
