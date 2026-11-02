# Packaging and code signing

This folder builds the Windows deliverables for PrintInterceptor:

| File | Purpose |
| --- | --- |
| `build-release.ps1` | Self-contained win-x64 publish, optional Authenticode signing, zip, and the Inno Setup `.exe`. |
| `install.ps1` | Per-till installer: config, ACLs, virtual printer, service or tray autostart. Also the uninstaller (`-Uninstall`). |
| `PrintInterceptor.iss` | Inno Setup 6 script used by `build-release.ps1`. |
| `../addprinter.ps1` | Creates/removes the virtual printer (called by `install.ps1`). |
| `../install-service.ps1` | Creates/removes the Windows service (called by `install.ps1`). |

Everything here runs on **Windows only**. The scripts are authored to be
PowerShell 5.1 compatible, but this repository's Linux build host has no
PowerShell, `signtool`, or Inno Setup, so the packaging path is checked by
review here and must be exercised once on a Windows build agent before release.

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

# Signed zip + signed setup .exe (PFX prompt for the password), Ghostscript bundled
# and the source offer filled in
.\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx `
    -SourceOfferContact "support@yourcompany.zw"

# Signed with a certificate from the Windows store
.\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567

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

`bin`, `lib` and `Resource` are copied into `publish\ghostscript`; they travel together
because Ghostscript finds its resources relative to the executable. The zip and the setup
`.exe` are built from `publish\*` recursively, so the bundle lands at `{app}\ghostscript`.
`install.ps1` writes that path into `ghostscriptPath` unless `-GhostscriptPath` was given
or the config already points at a working install, and the app probes
`<exe folder>\ghostscript\bin\gswin64c.exe` on its own, so a bundled copy works with no
config entry at all. `-StripGhostscriptFonts` drops `Resource\Font` and shrinks the package
at the risk of substitute glyphs on the printed invoice. The build prints the bundle's size,
and the zip and setup `.exe` grow by about that much — tens of megabytes, which is what a
self-contained package costs.

**Licensing — read this before the first customer-facing bundle.** Ghostscript is
AGPL-3.0. Putting its binaries in your package conveys them, which obliges whoever ships
it to carry the licence text *and* the corresponding source, or to hold a commercial
licence from Artifex. The build copies any licence file beside the binaries and writes
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
  timestamp (`-TimestampUrl`, default DigiCert).
- The setup `.exe` and its uninstaller: Inno signs them itself through
  `/Ssigntool`, so they are **not** signed a second time by the script.

Bundled Ghostscript binaries keep whatever signature Artifex shipped; the script
signs only `PrintInterceptor*.exe`.

The PFX password is converted from `SecureString` only in memory and is passed to
`signtool`/ISCC through their `/p` switches. Prefer a password-protected PFX on the
build agent and keep it out of the repository. When the issuer requires hardware
tokens, install the certificate with the vendor's CSP and use `-Thumbprint`.

## Install on a till

The setup `.exe` is the normal path. It asks for:

- **POS integration** — install the virtual printer `FiscalStack Fiscal Printer`
  on raw TCP `127.0.0.1:9100` (ticked by default).
- **Run mode** — Windows service (default) or tray app at sign-in.

`[Run]` then calls `install.ps1` elevated with the chosen options.

### Unattended install

```powershell
.\PrintInterceptor-1.2.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
    /TASKS="printer"
```

For a tray-only till add `/TASKS="printer,traymode"`.

### Direct script install (no Inno)

```powershell
# Service mode, printer and config under %ProgramData%\FiscalStack\PrintInterceptor
.\install.ps1 -ExePath .\publish\PrintInterceptor.exe

# Tray mode, real till printer named, API key kept out of config.json
.\install.ps1 -ExePath .\publish\PrintInterceptor.exe -RunMode Tray `
    -ReceiptPrinter "EPSON TM-T20III Receipt" -ApiKey "sk_live_..." `
    -TrayUser "TILL01\cashier" -StartApp

# Additional options
#   -PrinterMode PostScript -GhostscriptPath "C:\Program Files\gs\gs10.03.1\bin\gswin64c.exe"
#   -ApiEndpoint https://fiscalstack.co.zw/api/v1/ -A4Printer "HP LaserJet"
#   -SkipPrinter          (POS is already pointed at an existing port)
#   -SkipAutostart        (tray mode without the Run key)
```

`install.ps1`:

1. Writes/merges `config.json`. Existing keys are kept, so upgrades do not clobber
   layout, parsing, or offline settings. Unknown keys are preserved.
2. Stores `-ApiKey` as the machine environment variable `PRINTINTERCEPTOR_API_KEY`
   (read at startup, overrides `apiKey` in the file). The key never lands in
   `config.json`.
3. Restricts the config and data folders to Administrators, SYSTEM, and — in tray
   mode — the tray user (`icacls` with well-known SIDs, so it works on non-English
   Windows).
4. Creates the virtual printer through `addprinter.ps1` unless `-SkipPrinter`.
5. Removes any previous `PrintInterceptor` service, then installs the service or
   writes the **HKLM** `Run` value (`PrintInterceptorTray`). The tray app itself is
   started only with `-StartApp`, or interactively by the setup's *Start
   PrintInterceptor now* checkbox; silent installs rely on the next sign-in so a
   second listener is not left in an invisible session.

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

## Verification checklist (Windows)

The following is **not** covered by the Linux test suite and must be checked once
per release on a Windows machine:

1. `build-release.ps1 -Version <v> -PfxPath <pfx> -SourceOfferContact <address>` completes and
   creates the zip and setup `.exe`. Open the zip: `ghostscript\bin\gswin64c.exe`,
   `ghostscript\Resource`, the licence text and `ghostscript\SOURCE-OFFER.txt` are inside, and
   the offer names the version that shipped plus the contact passed to `-SourceOfferContact`. On a host with no
   Ghostscript the same command must **fail** with the "No 64-bit Ghostscript to bundle"
   message — that is the guard, not a bug to work around by adding `-NoGhostscript`.
2. `signtool verify /pa /v dist\PrintInterceptor-<v>-setup.exe` and the same for
   `publish\PrintInterceptor.exe` report a valid timestamped signature.
3. Silent install on a clean VM: printer and port exist, `Get-Service
   PrintInterceptor` is running, `PRINTINTERCEPTOR_API_KEY` is set at machine
   scope, and only Administrators/SYSTEM can read `config.json`.
4. Print a POS receipt to the virtual printer: it is fiscalised and printed on the
   real till printer; the tray/jobs window and *Fiscal status* agree with
   FiscalBridge's Fiscal Device page. Do it once with `install.ps1 -PrinterMode PostScript`
   on a VM that never had Ghostscript, to prove the bundled `{app}\ghostscript` is the copy
   being decoded rather than a system install.
5. Parse profiles: `install.ps1` reports the profiles shipped in `{app}\profiles`,
   Settings lists **sage** and **zohobooks**, *Use this profile* writes their rules into
   `config.json`, and *Import…* / *Save current rules as…* round-trip a file — including
   `parse.taxRules` and `parse.corrections`, which must survive the trip unchanged. Applying
   one needs an app/service restart before receipts are read with it.
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

- The installer does not bundle an API key. Pass `-ApiKey` at install time (or set
  `PRINTINTERCEPTOR_API_KEY` by other means). Installing from a shared script with
  the key baked in would expose it to anyone with the installer.
- Inno's `/Ssigntool` command and `install.ps1`'s argument line are built as single
  strings and passed verbatim, because PowerShell 5.1 mangles embedded quotes when
  splatting an array to a native program.
- `PrintInterceptor.iss` targets plain `x64` so it compiles on any Inno Setup 6.x;
  `x64compatible` requires 6.3+.
