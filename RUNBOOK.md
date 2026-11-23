# PrintInterceptor operations runbook

All paths below are on the till. `%ProgramData%\FiscalStack\PrintInterceptor` is abbreviated
as **`ROOT`**. The service is named `PrintInterceptor` unless installed with `-ServiceName`.

Golden rule: fiscal state (`ROOT\data\state`) and captured jobs (`ROOT\data\jobs`) are
authoritative. Never edit `ledger.json`, `fiscal-chain.json`, an offline queue file or a
print snapshot by hand to "fix" a number — reconcile with FiscalStack instead.

## Health checks

```powershell
Get-Service PrintInterceptor | Format-List Name,Status,StartType
Test-NetConnection 127.0.0.1 -Port 9100                 # capture port from config
Get-Printer -Name "FiscalStack Fiscal Printer" | Format-List Name,PortName,DriverName
Get-PrinterPort -Name FiscalStack_Raw_9100 | Format-List Name,PrinterHostAddress,PortNumber

# Service lifecycle, warnings and fatal errors (Event IDs below)
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='PrintInterceptor' } -MaxEvents 30 |
    Select-Object TimeCreated,Id,LevelDisplayName,Message

# Headless fiscal position / accountant export
& 'C:\Program Files\FiscalStack\PrintInterceptor\PrintInterceptor.exe' --day-report
```

Healthy means: service **Running**, port listening, virtual printer present, no fresh
`ERROR` lines in the current log, no `AmbiguousFailure` or stuck `InFlight` entries in the
console's **Jobs** page, and the fiscal day/device shown by the console's *Fiscal status* page
matches FiscalStack.

The tray icon is green when the API is reachable and the offline queue is empty, amber when
receipts are waiting to sync, red when the API is down or sync is paused.

## Logs

- File: `ROOT\data\logs\printinterceptor-yyyyMMdd.log`.
- On rollover: `ROOT\data\logs\printinterceptor-yyyyMMdd-HHmmss[-n].log`.
- Rotation: 10 MiB per file, at most 14 files and 14 days.
- Windows ACL: Administrators, SYSTEM and the account running the process only.
- Application Event Log source `PrintInterceptor`: `100` start, `200` stop, `300/301`
  warning, `500` fatal/install failure.

Ghostscript diagnostics are flattened, stripped of paths and key/value secrets and capped
before they are logged; a truncated line is expected, not corruption.

## Backup and restore

Back up while the service is stopped so `ledger.json` and `fiscal-chain.json` are consistent:

```powershell
Stop-Service PrintInterceptor
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$dest  = "D:\fiscal-backups\$stamp"
New-Item -ItemType Directory -Path $dest -Force | Out-Null

Copy-Item "$env:ProgramData\FiscalStack\PrintInterceptor" -Destination $dest -Recurse
icacls.exe "$env:ProgramData\FiscalStack\PrintInterceptor" /save "$dest\acl.txt" /T
Start-Service PrintInterceptor
```

Include `ROOT\secrets\*.protected` (DPAPI device key), `ROOT\config.json`,
`ROOT\profiles\*.json`, all of `ROOT\data\state`, `ROOT\data\jobs` and `ROOT\data\logs`.
DPAPI blobs only decrypt on the same Windows machine and account that created them; a restore
to another till must register the API key again (`PrintInterceptor.exe --register-key`, key on
stdin) and provision offline state from FiscalStack.

Restore: stop the service, copy the tree back, restore ACLs (`icacls.exe ROOT /restore acl.txt`),
re-register the API key with `PrintInterceptor.exe --register-key` if this is a different machine
or account, then start and run the health checks. If fiscal
state was restored from an older backup, verify outstanding receipts in FiscalStack before
allowing new sales — a rolled-back chain can collide with live counters.

## Offline queue drain

1. Confirm API reachability and that the fiscal day is **open**. Queued receipts must drain
   *before* the day is closed; after a close/reopen the daily counters no longer line up and
   later queued receipts are rejected.
2. Check `ROOT\data\state`:

| Path | Meaning |
| --- | --- |
| `offline-queue\*.json` | Signed receipts waiting for sync, oldest first. |
| `offline-failed\*.json` | Rejected receipts; never auto-retried. |
| `offline-sync-pause.json` | Sync paused after a rejection. |

3. Run *Sync offline receipts now* from the console's *Files & tools* page, or leave the service running (it retries
   every 60 seconds and tries once before each new sale). Watch the log for sync results and
   confirm the queue empties.
4. If paused: reconcile the head receipt in FiscalStack first, then use *Resume offline sync*
   (preferred) or delete `offline-sync-pause.json` deliberately. Do **not** delete queue files
   to skip a receipt — counters are strict and cannot skip numbers.
5. An ambiguous sync leaves the head receipt in place and blocks the queue. Verify in
   FiscalStack whether the receipt exists before allowing another attempt.
6. After a fiscal-day boundary rejection, the remaining receipts must be re-issued from the
   POS; the printed offline slips have no ZIMRA counterpart until then.

## Automated update (recommended)

When the `FiscalStack PrintInterceptor Updater` scheduled task is installed, updates are
handled without an operator at the till. Verify it is present and enabled:

```powershell
Get-ScheduledTask -TaskName "FiscalStack PrintInterceptor Updater" |
    Select-Object TaskName,State
Get-ScheduledTaskInfo -TaskName "FiscalStack PrintInterceptor Updater" |
    Select-Object LastRunTime,LastTaskResult,NextRunTime
```

The updater only runs when `PrintInterceptor.exe --status --json` reports `healthy: true`,
`pendingOffline: 0`, `recoveryPending: 0`, `offlinePaused: false`, no stale
`InFlight` entries and a live capture listener. It refuses to auto-update a tray-mode
till; see `packaging/README.md` for the task registration command and the
signature/hash gates. Every attempt writes Event ID `700` (success/skip) or `702`
(failure and automatic rollback) under the `PrintInterceptor` source.

Update artifacts and the pre-update backup live under
`ROOT\updates`. The backup folder printed in the log contains `app\`, `data\`,
`secrets\`, `profiles\`, `config.json`, `root-acl.txt` and `backup.json`. If an update
fails verification, `rollback.ps1` is called automatically. To roll back by hand:

```powershell
& "${env:ProgramFiles}\FiscalStack\PrintInterceptor\updater\rollback.ps1" `
    -BackupPath "$env:ProgramData\FiscalStack\PrintInterceptor\updates\backup-<version>-<stamp>"
```

After any automatic update or rollback, still run the health checks at the top of this
runbook and print one test receipt. Do not delete `ROOT\updates` until the release has
been accepted on the estate.

Do not promote a release until the `Windows - service, installer and Ghostscript` CI job
has passed. It builds and signs the release, executes bundled Ghostscript, runs the
silent installer, starts the service and verifies the capture listener on a real
Windows agent. The same check can be run on an elevated maintenance machine with
`packaging\verify-windows-integration.ps1`.

## Manual upgrade

1. Back up as above and record the current version/hash of the published folder.
2. Stop the service. Build/publish a new release with the pinned SDK in `global.json`:
   ```powershell
   dotnet publish src\PrintInterceptor -c Release -r win-x64 --self-contained true -o publish
   ```
   or run the packaged installer, which also refreshes the virtual printer and profiles.
3. Replace only the application binaries/Ghostscript bundle/profiles. Keep `ROOT\config.json`,
   `ROOT\secrets`, `ROOT\profiles\*` imported by the operator, and all of `ROOT\data`.
4. If the service account changed, run `.\install-service.ps1 -Remove` then reinstall with
   `-ServiceCredential` (see below). Otherwise leave the service registration alone.
5. Start the service and run the health checks, then print one test receipt end-to-end.
   Applying a parse profile in the console's **Parse rules** page rebuilds the live parser immediately; no restart is needed.

Service install with a dedicated account (recommended):

```powershell
$cred = Get-Credential 'FISCALSTACK\svc-print'
.\install-service.ps1 -ServiceCredential $cred -RequireDedicatedAccount `
    -ConfigPath "$env:ProgramData\FiscalStack\PrintInterceptor\config.json"
```

The account needs logon-as-a-service (granted by `New-Service`), read access to the config,
read/write to the data folder and permission to print to the configured receipt/A4 printers.
Running as LocalSystem is allowed for compatibility but logs a loud warning and Event ID 300;
`-RequireDedicatedAccount` turns it into a hard failure.

## Rollback

1. Drain or reconcile the offline queue first if possible, then back up the *current* state.
2. Stop the service and restore the previous published folder (and Ghostscript bundle) from
   the release backup. Restore `ROOT\config.json` only if the new version changed its schema
   incompatibly — keep newer fiscal state wherever possible.
3. Reinstall the service only if its binary path or account changed; start it and run the
   health checks.
4. If `ledger.json` or `fiscal-chain.json` had to be restored from an older backup, do not
   delete them to "unblock" startup. Verify with FiscalStack which receipts exist, reconcile
   unknown ones, then resume. Never rewind counters that ZIMRA has already issued.

## Ghostscript and printer verification

Ghostscript is only needed for PostScript/PDF capture and `printing.template = "original"`.
The resolver accepts only a trusted path: bundled `{app}\ghostscript\bin\gswin64c.exe`, a
Program Files install, or a trusted OS path on Linux. A `ghostscriptPath` pointing into a user
profile, `Temp`, `ProgramData` or another writable folder is rejected and logged; fix the
install rather than loosening the ACL. Symlinks/junctions are resolved before the check.

```powershell
$gs = "${env:ProgramFiles}\FiscalStack\PrintInterceptor\ghostscript\bin\gswin64c.exe"
if (Test-Path $gs) {
    & $gs -q -dNOPAUSE -dBATCH -dSAFER -sDEVICE=txtwrite -sOutputFile=- .\samples\pos-receipt.txt
}
```

Expect exit code 0 and no `-dSAFER` denial in the log. If the bundle is absent, install
Ghostscript under Program Files and leave `ghostscriptPath` empty, or set it to that trusted
`gswin64c.exe`. A `Generic / Text Only` capture printer needs no Ghostscript at all.

Printer checks:

```powershell
Get-Printer -Name "FiscalStack Fiscal Printer" | Format-List Name,PortName,DriverName
Get-PrinterPort -Name FiscalStack_Raw_9100 | Format-List Name,PrinterHostAddress,PortNumber
Get-Content .\samples\pos-receipt.txt -Raw | Out-Printer -Name "FiscalStack Fiscal Printer"
```

The sample should produce `Captured N bytes from 127.0.0.1:…` in the log, then a fiscal
receipt on the real till printer. Confirm the till printer is visible to the service account
(not just to the logged-in operator); a service running as LocalSystem sees a different set of
user printers. A4/original-template output additionally needs a valid `printing.a4PrinterName`
(or `printing.printerName`) and a working Ghostscript; failures fall back to the fiscal
template so the customer still gets a proof.
