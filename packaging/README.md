# Packaging and code signing

This folder builds the Windows deliverables for PrintInterceptor:

| File | Purpose |
| --- | --- |
| `build-release.ps1` | Self-contained win-x64 publish, optional Authenticode signing, zip, and the Inno Setup `.exe`. |
| `install.ps1` | Per-till installer: config, ACLs, virtual printer, service or tray autostart. Also the uninstaller (`-Uninstall`). |
| `PrintInterceptor.iss` | Inno Setup 6 script used by `build-release.ps1`. |
| `auto-update.ps1` | Verifies a signed manifest/installer, backs up, silently upgrades and rolls back. |
| `install-update-task.ps1` | Installs/removes the hourly `SYSTEM` scheduled task that runs the updater. |
| `rollback.ps1` | Restores the pre-update application folder and fiscal state. |
| `verify-windows-integration.ps1` | Elevated end-to-end smoke test: signed build, bundled Ghostscript, silent install, service, listener. |
| `../addprinter.ps1` | Creates/removes the virtual printer (called by `install.ps1`). |
| `../install-service.ps1` | Creates/removes the Windows service (called by `install.ps1`). |

Everything here runs on **Windows only**. The scripts are authored to be
PowerShell 5.1 compatible, but a Linux development host cannot exercise service
installation, Authenticode signing or Inno Setup. CI now runs those paths on a
real `windows-latest` agent in the `Windows - service, installer and Ghostscript`
job. To run the same validation locally from an elevated prompt:

```powershell
choco install innosetup ghostscript -y
.\packaging\verify-windows-integration.ps1 -PurgeOnCleanup
```

The verifier builds a signed release (creating a temporary trusted test
certificate when `-Thumbprint` is omitted), checks the update manifest hash and
Authenticode signature, executes the bundled Ghostscript with `txtwrite`, runs
the setup silently with the `ghostscript` task selected, then checks that the
installed `{app}\ghostscript\bin\gswin64c.exe` is configured and works, that the
service is `Running` and that the capture listener is bound. It removes the
service, install and test certificate afterwards. It is safe to point at a real
certificate with `-Thumbprint`; use `-SkipSigning` only for a packaging smoke test.

The test signs without an RFC 3161 timestamp by default so it does not depend on
a public timestamp server. Pass `-TimestampUrl https://timestamp.digicert.com`
to exercise timestamping as well.

## Prerequisites

- Windows 10/11 or Windows Server 2019+ build host (x64).
- .NET 8 SDK.
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) at the default location
  (`%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe`) for the setup `.exe`. Add
  `-SkipInstaller` to produce only the zip.
- Windows SDK for `signtool.exe` (or `signtool` on `PATH`) **only when signing**.
- An Authenticode code-signing certificate: either a `.pfx` file or a
  certificate installed in the machine store with a known thumbprint.
- Ghostscript is needed when the virtual printer uses the PostScript or PDF driver;
  `Generic / Text Only` capture does not need it. A release **bundles it by default** —
  [Ghostscript in the package](#ghostscript-in-the-package) covers which copy is used,
  how to name a specific install, and what AGPL-3.0 requires of whoever ships the result.

## Build a release

```powershell
# Unsigned zip only
.\packaging\build-release.ps1 -Version 1.2.0 -SkipInstaller

# Preferred: sign with a certificate already in the Windows store or an HSM
.\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567

# Signed zip + signed setup .exe from a PFX (SecureString prompt), Ghostscript
# bundled and the source offer filled in. The PFX is imported into the current-user
# store for this build; its password is never put on a command line.
.\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx `
    -SourceOfferContact "support@yourcompany.zw"

# Text-only deployment (Generic / Text Only driver): no Ghostscript in the package
.\packaging\build-release.ps1 -Version 1.2.0 -NoGhostscript

# Ghostscript copied from a named folder rather than the install found automatically
.\packaging\build-release.ps1 -Version 1.2.0 -GhostscriptDir "C:\Program Files\gs\gs10.08.0"
```

Outputs (under `..\dist` by default, override with `-OutputRoot`):

- `publish\` — the self-contained application plus the three install scripts, the
  shipped `profiles\` folder (the read-only receipt layouts Settings and the tray list;
  see [Parsing profiles](../README.md#parsing-profiles)), and `ghostscript\` unless
  `-NoGhostscript` was used.
- `PrintInterceptor-<version>-win-x64.zip` — for manual/USB installs.
- `PrintInterceptor-<version>-setup.exe` — Inno Setup installer (when available).

Version handling: `-Version` becomes the informational version, so a suffix such as
`2026.10.31-dev` is allowed; `AssemblyVersion`/`FileVersion` receive the numeric
part only. Default version is the current date plus `-dev`.

### Auto-update artifacts

A signed setup build also writes `dist\update.json` (and a versioned copy):

```json
{
  "version": "1.4.0",
  "channel": "stable",
  "setupUrl": "PrintInterceptor-1.4.0-setup.exe",
  "sha256": "...",
  "signerThumbprint": "YOURCODE SIGNING THUMBPRINT",
  "publishedAt": "2026-11-22T10:00:00.0000000Z",
  "installTasks": "printer,ghostscript"
}
```

`installTasks` is generated from the release contents: a text-only build made with
`-NoGhostscript` publishes `"printer"` instead. `auto-update.ps1` passes the
manifest value to Setup so it never selects a `ghostscript` task that this release
does not contain; tray-mode updates add `traymode` to that same list.

Host `update.json` and the setup file over **HTTPS**. `-UpdateBaseUrl` sets the
public prefix when the download is served from somewhere other than the manifest
directory; without it a relative `setupUrl` is resolved beside the manifest.
Keep old setup files on the server so an operator can reinstall one manually.
`-Channel` lets you publish `canary` and `stable` manifests from separate URLs.

The publish folder ships the updater at `{app}\updater`. It is **not enabled by
the installer**: enabling updates is an explicit deployment decision.

### Ghostscript in the package

`build-release.ps1` bundles 64-bit Ghostscript unless `-NoGhostscript` is passed, so a
PostScript or PDF till needs no separate install. It never downloads anything: a release
build pulling a third-party binary off the network is a supply-chain risk, so the source
has to be on the build host already. The copy is chosen in this order:

1. the folder named by `-GhostscriptDir` (must contain `bin\gswin64c.exe`);
2. the install `gswin64c.exe` resolves to on `PATH`;
3. the newest `gs*` folder under `%ProgramFiles%\gs` or `%ProgramFiles(x86)%\gs`.

If none exists the build **stops** with a message naming all three escapes — installing
the official release on the build host, `-GhostscriptDir`, `-NoGhostscript` — rather than
quietly producing a package that cannot decode a PostScript job.

At runtime the **bundled copy is preferred**. A configured `ghostscriptPath` is honoured
only when it resolves (including symlinks/junctions) inside the application folder, Program
Files, or trusted OS locations on Linux (`/usr`, `/opt`); paths under a user profile and
arbitrary `PATH` entries are rejected and logged. The runner rechecks the final target
before every launch. Ghostscript parses untrusted print jobs while the service may run
elevated, so a writable path is a code-execution risk, not just a configuration mistake.

`bin`, `lib` and `Resource` are copied into `publish\ghostscript`; they travel together
because Ghostscript finds its resources relative to the executable. The zip contains that
folder unconditionally. In the setup `.exe` it is a separate, ticked-by-default task: the
always-installed `[Files]` entry excludes `\ghostscript\*`, and a conditional entry copies
it only when the `ghostscript` task is selected. This keeps the AGPL choice visible to the
operator. `install.ps1` writes `{app}\ghostscript\bin\gswin64c.exe` into `ghostscriptPath`
unless `-GhostscriptPath` was given or the config already points at a working install, and
the app probes
`<exe folder>\ghostscript\bin\gswin64c.exe` on its own, so a bundled copy works with no
config entry at all. `-StripGhostscriptFonts` drops `Resource\Font` and shrinks the package
at the risk of substitute glyphs on the printed invoice. The build prints the bundle's size,
and the zip and setup `.exe` grow by about that much — tens of megabytes, which is what a
self-contained package costs.

**Licensing — read this before the first customer-facing bundle.** Ghostscript is
AGPL-3.0. Putting its binaries in your package conveys them, which obliges whoever ships
it to carry the licence text *and* the corresponding source, or to hold a commercial
licence from Artifex. The build copies a licence file from the install root or its `doc`
folder (official packages keep the AGPL text in `doc\COPYING`) and writes
`ghostscript\SOURCE-OFFER.txt`: the version bundled, where Artifex publishes the matching
source, and the address recipients should write to, taken from `-SourceOfferContact`. Omit
that and the file reads *NOT SET*. What the build produces is a **record** of a written
offer, not the offer itself — section 6 of AGPL-3.0 requires the distributor to honour it
for at least three years from the last distribution. So a release that ships the bundle is
not, on that basis alone, licence-compliant: fill in the contact, or hold a commercial
licence from Artifex.

The two routes that avoid the question are `-NoGhostscript` with Ghostscript installed on
the till, and the `Generic / Text Only` capture path, which needs no Ghostscript at all.

### What gets signed

- Every `PrintInterceptor*.exe` in the publish folder, with SHA-256 and an RFC 3161
  timestamp (`-TimestampUrl`, default `https://timestamp.digicert.com`).
- The setup `.exe` and its uninstaller: Inno signs them itself through
  `/Ssigntool`, so they are **not** signed a second time by the script.

Bundled Ghostscript binaries keep whatever signature Artifex shipped; the script
signs only `PrintInterceptor*.exe`.

Signing always uses `/sha1 <thumbprint>` with a certificate in the Windows user or
machine store. With `-Thumbprint` the certificate (including an HSM CSP/KSP) is used
as installed. With `-PfxPath` the script imports the PFX into `Cert:\CurrentUser\My`
using the `-PfxPassword` `SecureString`, signs by thumbprint, and removes the temporary
certificate afterwards. Neither `signtool` nor ISCC ever receives `/p` or a plaintext
password, and the PFX never goes into the repository. Prefer `-Thumbprint` where possible.

## Install on a till

The setup `.exe` is the normal path. It asks for:

- **POS integration** — install the virtual printer `FiscalStack Fiscal Printer`
  on raw TCP `127.0.0.1:9100` (ticked by default).
- **Optional components** — bundled 64-bit Ghostscript for PDF/PostScript capture
  and original-invoice stamping (ticked by default when the release contains it).
  It is a separate selectable component because the bundle is AGPL-3.0; see
  [Ghostscript in the package](#ghostscript-in-the-package).
- **Run mode** — Windows service (default) or tray app at sign-in.

`[Run]` then calls `install.ps1` elevated with the chosen options.

### Unattended install

```powershell
.\PrintInterceptor-1.2.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
    /TASKS="printer,ghostscript"
```

For a tray-only till add `/TASKS="printer,ghostscript,traymode"`. Omit
`ghostscript` only when the till uses `Generic / Text Only` capture and never
stamps original invoices; without the task the bundle is not installed and no
`ghostscriptPath` is configured.

### Direct script install (no Inno)

```powershell
# Service mode, printer and config under %ProgramData%\FiscalStack\PrintInterceptor
.\install.ps1 -ExePath .\publish\PrintInterceptor.exe

# Tray mode, real till printer named. Register the key interactively afterwards.
.\install.ps1 -ExePath .\publish\PrintInterceptor.exe -RunMode Tray `
    -ReceiptPrinter "EPSON TM-T20III Receipt" -TrayUser "TILL01\cashier" -StartApp

# Register the API key (hidden prompt, no key on any command line; restart afterwards)
.\publish\PrintInterceptor.exe --register-key
# The tray's Settings dialog is the equivalent GUI path.

# Additional options
#   -PrinterMode PostScript   (bundled Ghostscript is preferred; see above)
#   -ApiEndpoint https://fiscalstack.co.zw/api/v1/ -A4Printer "HP LaserJet"
#   -SkipPrinter              (POS is already pointed at an existing port)
#   -SkipAutostart            (tray mode without the Run key)
```

`install.ps1`:

1. Restricts the root folder before writing anything, then writes/merges `config.json`.
   Existing keys are kept, so upgrades do not clobber layout, parsing, or offline settings.
   Unknown keys are preserved.
2. Restricts the config and data folders to Administrators, SYSTEM, and — in tray
   mode — the tray user (`icacls` with well-known SIDs, so it works on non-English
   Windows). Every explicit ACE is cleared first, so a Service↔Tray switch does not
   leave the previous user's grant behind.
3. Creates the virtual printer through `addprinter.ps1` unless `-SkipPrinter`.
4. Removes any previous `PrintInterceptor` service, then installs the service or
   writes the **HKLM** `Run` value (`PrintInterceptorTray`). The tray app itself is
   started only with `-StartApp`, or interactively by the setup's *Start
   PrintInterceptor now* checkbox; silent installs rely on the next sign-in so a
   second listener is not left in an invisible session.
5. The API key is registered separately with `--register-key` or Settings. `-ApiKey`
   is deprecated and only writes the machine environment variable; see
   [Secret management](#secret-management).

### Uninstall

The Inno uninstaller calls `install.ps1 -Uninstall`, which stops and deletes the
service, removes the printer and port, and removes the tray autostart. Configuration
and fiscal data are **kept** by default, because they contain the cached device key,
the offline queue and the job ledger.

```powershell
# Remove the application, keep the fiscal data
.\install.ps1 -Uninstall

# Also delete config, cached keys, jobs and the machine API key
.\install.ps1 -Uninstall -Purge
```

Uninstalling is not the same as de-registering the till: fiscal receipts already
issued stay valid, but any queued offline receipts in `data\state` are lost with
`-Purge`. Sync them first from the tray (*Sync offline receipts now*).

## Enable auto-update

On each till, after the application has been installed and its key registered:

```powershell
"C:\Program Files\FiscalStack\PrintInterceptor\updater\install-update-task.ps1" `
    -ManifestUrl "https://updates.example.com/printinterceptor/stable/update.json" `
    -SignerThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 `
    -Channel stable -IntervalMinutes 60
```

This registers a `SYSTEM` task that runs every hour (first run 10 minutes after
registration). The manifest URL and thumbprint are stored in the task definition;
neither is a secret. Remove the task with `-Remove`.

Every run:

1. Fetches the manifest over HTTPS and refuses a channel mismatch.
2. Downloads the setup to `%ProgramData%\FiscalStack\PrintInterceptor\updates`,
   checks SHA-256, and requires a **valid Authenticode signature from the pinned
   thumbprint** before executing anything.
3. Calls `PrintInterceptor.exe --status --json` and refuses to continue when the
   offline queue, recovery records, sync pause, stale in-flight jobs or ledger
   alerts say the fiscal position is not clean.
4. Backs up the application folder, `config.json`, `secrets`, `profiles` and
   `data` (plus ACLs), stops the service, runs the setup with
   `/VERYSILENT /NORESTART`, and waits for the service and capture listener.
5. Verifies the installed version increased and the new status is healthy. Any
   failure calls `rollback.ps1` automatically, restoring the pre-update app and
   fiscal state and restarting the service.

Useful controls:

```powershell
# Verify a release without touching the till
.\updater\auto-update.ps1 -ManifestUrl "https://.../update.json" `
    -SignerThumbprint 0123... -DryRun

# Roll back manually from the backup printed by the updater
.\updater\rollback.ps1 -BackupPath "C:\ProgramData\FiscalStack\PrintInterceptor\updates\backup-1.3.0-..."

# Remove the scheduled task
.\updater\install-update-task.ps1 -Remove
```

Service mode is required by default. A tray-mode till cannot be stopped
reliably by a `SYSTEM` task, so `auto-update.ps1` refuses it unless
`-AllowTrayUpdate` is passed deliberately; even then an operator must sign in
afterwards to restart the tray app. `-Force` bypasses health/version gates for
emergency repair only — it does **not** bypass signature or hash verification.

The health probe itself is available to support:

```powershell
& "C:\Program Files\FiscalStack\PrintInterceptor\PrintInterceptor.exe" --status --json
```

## Service or tray?

| | Windows service | Tray app |
| --- | --- | --- |
| Starts before sign-in | yes | no |
| Status icon / Fiscal status window | no (session 0) | yes |
| Runs as | LocalSystem | the signed-in user |
| Recommended for | unattended tills | attended tills that need the device view |

Choose one mode per till. Installing as a service and also starting the tray app
would create two listeners fighting for port 9100, so `install.ps1` removes the
service when switching to tray (and removes the autostart when switching to
service).

## Secret management on a till

- **Register, don't paste:** run `PrintInterceptor.exe --register-key` after install
  (hidden prompt, also accepts piped stdin) or use the tray **Settings → API key**.
  The key is never on a command line, in shell history, or in `config.json`; `Save`
  writes a blank `apiKey` atomically and keeps the live value in memory.
- **Protection:** the API key and the ZIMRA device key are `LocalMachine` DPAPI blobs
  under `<config folder>\secrets\`, each with an ACL limited to SYSTEM, Administrators and
  the till account. DPAPI protects copies; the ACL protects local readers of the machine
  master key. The config/data/secrets folders are restricted by the installer and on save,
  and the app fails closed if an ACL cannot be applied.
- **Rotation:** generate a new key in FiscalStack, run `--register-key` again (it replaces
  the blob), restart the service/tray, then revoke the old key. `--clear-key` removes the
  local copy. Remove any legacy `PRINTINTERCEPTOR_API_KEY` machine variable after
  registering; it is registry-readable and `-ApiKey` is deprecated for that reason.
- **Offline key:** provision with **Settings → Refresh from FiscalStack** while online.
  The private key goes to `secrets\device-key.protected`; `offline-provisioning.json`
  holds only state. A legacy plaintext key in that cache is stripped and never used. If
  the store is unavailable or cannot be written, offline signing fails with an actionable
  error instead of falling back to the file.

See the [main README's Secret management section](../README.md#secret-management) for the
full policy.

## Verification checklist (Windows)

The following is **not** covered by the Linux test suite and must be checked once
per release on a Windows machine:

1. `build-release.ps1 -Version <v> -PfxPath <pfx> -SourceOfferContact <address>` (or
   `-Thumbprint <store cert>`, preferred) completes and creates the zip and setup `.exe`.
   With a PFX, watch for the import message and confirm no `/p` password appears in the
   log. Open the zip: `ghostscript\bin\gswin64c.exe`,
   `ghostscript\Resource`, the licence text and `ghostscript\SOURCE-OFFER.txt` are inside, and
   the offer names the version that shipped plus the contact passed to `-SourceOfferContact`. On a host with no
   Ghostscript the same command must **fail** with the "No 64-bit Ghostscript to bundle"
   message — that is the guard, not a bug to work around by adding `-NoGhostscript`.
2. `signtool verify /pa /v dist\PrintInterceptor-<v>-setup.exe` and the same for
   `publish\PrintInterceptor.exe` report a valid timestamped signature.
3. Silent install on a clean VM: printer and port exist, `Get-Service
   PrintInterceptor` is running, `--register-key` stores the key in
   `%ProgramData%\FiscalStack\PrintInterceptor\secrets\`, and only Administrators/SYSTEM
   (plus the tray user in tray mode) can read `config.json` and the secrets folder.
   Switching Service↔Tray leaves no stale explicit ACE for the previous user.
4. Print a POS receipt to the virtual printer: it is fiscalised and printed on the
   real till printer; the tray/jobs window and *Fiscal status* agree with
   FiscalBridge's Fiscal Device page. Do it once with `install.ps1 -PrinterMode PostScript`
   on a VM that never had Ghostscript, to prove the bundled `{app}\ghostscript` is the copy
   being decoded rather than a system install.
5. Parse profiles: `install.ps1` reports the profiles shipped in `{app}\profiles`,
   Settings lists **sage**, **zohobooks** and **palladium**, *Use this profile* writes their rules into
   `config.json`, and *Import…* / *Save current rules as…* round-trip a file — including
   `vatInclusive`, `parse.taxRules` and `parse.corrections`, which must survive the trip
   unchanged. Applying one needs an app/service restart before receipts are read with it.
6. Tax markers: print a till receipt with one exempt line (a code after the amount, e.g.
   `5.00 EX`) and one ordinary line. The fiscal receipt prints the exempt line under the
   exemption tax ID and the other under the standard rate; the jobs window shows no warning
   for the marked line and the *no marker* note only if the POS prints codes on some lines
   and not others.
7. Returns: print a refund slip from the POS. With its original invoice number printed on it,
   it fiscalises as a credit note, prints a negative total on the fiscal receipt, and does not
   touch the sale it reverses; without one it is held for review and the reason names
   `parse.corrections.originalInvoiceLabels`. Then reprint the *same* sale a second time and
   confirm the ledger logs `DUPLICATE PRINT SUPPRESSED` with `type=FiscalInvoice` instead of
   sending a second request.
8. Reconcile: with a **test** key, make a fiscalisation whose answer the till cannot read —
   print while the network cable is pulled, or with `apiEndpoint` aimed at a port that accepts
   and never answers. The job lands as `AmbiguousFailure` and its invoice number stays blocked.
   Restore connectivity and choose *Reconcile unknown receipts now*: its balloon reports
   adopted / re-sent / set aside / still unknown, the job window then shows the sale
   `Fiscalised`, and exactly one receipt exists in FiscalStack for it. Repeat with
   `reconcile.autoResend` `false` and confirm nothing is sent at all — only a receipt the
   server already has gets settled.
9. Day report: on a till that printed today, *Daily Z-shift summary…* lists its documents, and the
   bucket totals agree with the tax rows on the printed receipts. Open a day with no sales: the
   summary says nothing was issued instead of showing a zero day that reads like a nil return.
   **Save summary…** writes `fiscal-day-<date>.txt`; **Export lines (CSV)…** opens in Excel with
   one row per printed line, fiscal code and receipt number filled, and credit-note lines signed —
   and a line typed as `=1+1` at the counter stays text. Then, on a service-only till, run
   `PrintInterceptor.exe --day-report=<yyyy-MM-dd> --csv=D:\fiscal\lines.csv` and confirm the exit
   code is 0 (`$LASTEXITCODE`) and stdout holds the report alone, so `>` can redirect it. Delete
   `state\print-snapshots` and confirm the report still reads the day from `fiscalised\`.
10. Capture a profile: on a till whose `profiles` folder holds only the shipped two, print from an
    unknown POS layout (rename a fixture's labels first, e.g. `SLIP NO:` / `SOLD TO:` / a
    `Item Qty Price Disc % VAT % Total` header) so the job lands in **Needs review**, then open
    *Capture receipt layout from a job…*. The job is in the dropdown with its text, the left reading
    shows *— not read —* where the right one shows the value and its line, and the rules listed are
    only the additions — no `productEndLine` for a footer word that also names a column. **Save
    profile** writes `<id>.json` and Settings lists it; **Save and use now** also writes the rules
    into `config.json` with `parse.profile` set, and after a restart the same receipt fiscalises.
    Paste a receipt with a tax code the rules do not name (e.g. `6.00 G`) and confirm it is reported
    under *What a profile cannot decide* instead of becoming a rule.
11. Tray mode: the Run value starts the app at sign-in for the intended user.
12. Uninstall leaves the service, printer and Run value gone; `-Purge` also removes
   `%ProgramData%\FiscalStack\PrintInterceptor`.

## Notes and open items

- The installer does not bundle an API key. After installing, run
  `PrintInterceptor.exe --register-key` (hidden prompt) or use the tray Settings.
  `-ApiKey` and `PRINTINTERCEPTOR_API_KEY` are deprecated fallbacks: a key baked
  into a shared script or registry value is readable by anyone with access to it.
- Inno's `/Ssigntool` command and `install.ps1`'s argument line are built as single
  strings and passed verbatim, because PowerShell 5.1 mangles embedded quotes when
  splatting an array to a native program.
- `PrintInterceptor.iss` targets plain `x64` so it compiles on any Inno Setup 6.x;
  `x64compatible` requires 6.3+.
