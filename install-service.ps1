# Installs PrintInterceptor as a Windows service.
# Run PowerShell as Administrator.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$ExePath = "",
    [string]$ConfigPath = "",
    [string]$ServiceName = "PrintInterceptor",
    [string]$DisplayName = "FiscalStack Print Interceptor",
    [PSCredential]$ServiceCredential,
    [switch]$RequireDedicatedAccount,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$script:EventSource = "PrintInterceptor"
$script:EventLogName = "Application"

function Invoke-Sc([string]$arguments) {
    # Start-Process takes this string verbatim, so quoted values survive intact.
    $process = Start-Process -FilePath "sc.exe" -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    return $process.ExitCode
}

function Ensure-EventSource {
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($script:EventSource)) {
            [System.Diagnostics.EventLog]::CreateEventSource($script:EventSource, $script:EventLogName)
        }
    }
    catch {
        Write-Warning "Could not register Event Log source '$($script:EventSource)': $($_.Exception.Message)"
    }
}

function Write-ServiceEvent([string]$EntryType, [int]$EventId, [string]$Message) {
    # Diagnostics must never turn a successful install into a failure.
    try {
        Write-EventLog -LogName $script:EventLogName -Source $script:EventSource `
            -EntryType $EntryType -EventId $EventId -Message $Message -ErrorAction Stop | Out-Null
    }
    catch { }
}

function Grant-AccountAccess([string]$path, [string]$account) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    # (OI)(CI) only applies to directories; icacls /T would pass it to files
    # too and skip them, leaving a dedicated service account without access to
    # config.json or the ledger files.
    $targets = @(Get-Item -LiteralPath $path -Force)
    if ($targets[0].PSIsContainer) {
        $targets += @(Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue)
    }

    foreach ($target in $targets) {
        $flags = if ($target.PSIsContainer) { "(OI)(CI)" } else { "" }
        & icacls.exe $target.FullName "/grant:${account}:${flags}F" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not grant '$account' access to $($target.FullName) (icacls exit $LASTEXITCODE). The service may not be able to read its config or write its state."
        }
    }
}

Ensure-EventSource

trap {
    Write-ServiceEvent -EntryType Error -EventId 500 `
        -Message "PrintInterceptor service management failed: $($_.Exception.Message)"
    throw
}

if ($Remove) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne "Stopped") {
        Write-ServiceEvent -EntryType Information -EventId 200 `
            -Message "PrintInterceptor service '$ServiceName' was stopped during removal."
    }

    Start-Sleep -Seconds 2
    if ((Invoke-Sc "delete `"$ServiceName`"") -ne 0) {
        $message = "Could not delete service '$ServiceName'."
        Write-ServiceEvent -EntryType Error -EventId 500 -Message $message
        throw $message
    }

    Write-Host "Service '$ServiceName' removed."
    exit 0
}

if ($null -eq $ServiceCredential) {
    if ($RequireDedicatedAccount) {
        $message = "Refusing to install '$ServiceName' as LocalSystem: -RequireDedicatedAccount was supplied. " +
                   "Pass -ServiceCredential (Get-Credential) for a dedicated low-privilege service account."
        Write-ServiceEvent -EntryType Error -EventId 500 -Message $message
        throw $message
    }

    Write-Warning "LOCALSYSTEM: '$ServiceName' will run as LocalSystem, which can see every printer and has full machine rights. For production, pass -ServiceCredential (Get-Credential) for a dedicated low-privilege account, or -RequireDedicatedAccount to refuse this default."
    Write-ServiceEvent -EntryType Warning -EventId 300 `
        -Message "PrintInterceptor was installed as LocalSystem. Use a dedicated low-privilege account on production tills."

    $account = $null
}
else {
    $account = $ServiceCredential.UserName
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "publish\PrintInterceptor.exe"
}

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath. Publish first: dotnet publish src\PrintInterceptor -c Release -r win-x64 --self-contained true -o publish"
}

$exe = (Resolve-Path $ExePath).Path
$binPath = "`"$exe`""

$explicitConfig = -not [string]::IsNullOrWhiteSpace($ConfigPath)
if ($explicitConfig) {
    # The service starts from System32: always store an absolute config path.
    $config = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
    $binPath = "$binPath --config `"$config`""
}
else {
    $config = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor\config.json"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Remove it first with: .\install-service.ps1 -Remove"
}

# New-Service passes the binary path straight to the service manager, so the
# quotes around the exe and config paths are not re-parsed by a shell.
$newService = @{
    Name           = $ServiceName
    BinaryPathName = $binPath
    DisplayName    = $DisplayName
    StartupType    = "Automatic"
}
if ($null -ne $ServiceCredential) {
    $newService["Credential"] = $ServiceCredential
}

try {
    New-Service @newService | Out-Null
}
catch {
    $message = "Could not create service '$ServiceName': $($_.Exception.Message)"
    Write-ServiceEvent -EntryType Error -EventId 500 -Message $message
    throw $message
}

$description = "Captures POS print jobs from a virtual printer, fiscalises them and prints the ZIMRA fiscal receipt."
if ((Invoke-Sc "description `"$ServiceName`" `"$description`"") -ne 0) {
    Write-Warning "The service was created, but its description could not be set."
}

# Four restart attempts with a one-minute gap; the last one is the final recovery
# action, after which the service is left stopped for an operator to investigate.
if ((Invoke-Sc "failure `"$ServiceName`" reset= 86400 actions= restart/60000/restart/60000/restart/60000/restart/60000") -ne 0) {
    Write-Warning "The service was created, but restart-on-failure recovery could not be configured."
    Write-ServiceEvent -EntryType Warning -EventId 301 `
        -Message "Could not configure restart-on-failure recovery for service '$ServiceName'."
}

if ($null -ne $account) {
    # The packaged installer restricts these folders to Admins/SYSTEM; a dedicated
    # service account needs an explicit grant before it can read config or save state.
    Grant-AccountAccess (Split-Path $config -Parent) $account

    $dataDirectory = Join-Path $env:ProgramData "FiscalStack\PrintInterceptor\data"
    if (Test-Path $config) {
        try {
            $stored = Get-Content -Path $config -Raw | ConvertFrom-Json
            if ($stored.storage.dataDirectory) {
                $dataDirectory = $stored.storage.dataDirectory
            }
        }
        catch { }
    }

    Grant-AccountAccess $dataDirectory $account
}

try {
    Start-Service -Name $ServiceName
}
catch {
    $message = "Service '$ServiceName' was created but did not start: $($_.Exception.Message). Check the log under %ProgramData%\FiscalStack\PrintInterceptor\data\logs."
    Write-ServiceEvent -EntryType Error -EventId 500 -Message $message
    throw $message
}

Write-ServiceEvent -EntryType Information -EventId 100 `
    -Message "PrintInterceptor service '$ServiceName' started (account: $(if ($account) { $account } else { 'LocalSystem' }))."

Write-Host "Service '$ServiceName' installed and started."
Write-Host "Account: $(if ($account) { $account } else { 'LocalSystem (use -ServiceCredential for production)' })"
Write-Host "Configuration: $(if ($explicitConfig) { $config } else { '%ProgramData%\FiscalStack\PrintInterceptor\config.json' })"
Write-Host "Logs: %ProgramData%\FiscalStack\PrintInterceptor\data\logs"
Write-Host "Event Log: source '$($script:EventSource)' in the Application log (100 start, 200 stop, 300/301 warning, 500 fatal)"
