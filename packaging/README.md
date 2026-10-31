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
- Ghostscript is **not** bundled. It is needed only when the virtual printer uses
  the PostScript driver. Ghostscript is AGPL-licensed: shipping it inside a
  commercial installer creates obligations. The installer detects an existing
  64-bit install and warns if it is missing; install it separately.

## Build a release

```powershell
# Unsigned zip only
.\packaging\build-release.ps1 -Version 1.2.0 -SkipInstaller

# Signed zip + signed setup .exe (PFX prompt for the password)
.\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx

# Signed with a certificate from the Windows store
.\packaging\build-release.ps1 -Version 1.2.0 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

Outputs (under `..\dist` by default, override with `-OutputRoot`):

- `publish\` — the self-contained application plus the three install scripts.
- `PrintInterceptor-<version>-win-x64.zip` — for manual/USB installs.
- `PrintInterceptor-<version>-setup.exe` — Inno Setup installer (when available).

Version handling: `-Version` becomes the informational version, so a suffix such as
`2026.10.31-dev` is allowed; `AssemblyVersion`/`FileVersion` receive the numeric
part only. Default version is the current date plus `-dev`.

### What gets signed

- Every `PrintInterceptor*.exe` in the publish folder, with SHA-256 and an RFC 3161
  timestamp (`-TimestampUrl`, default DigiCert).
- The setup `.exe` and its uninstaller: Inno signs them itself through
  `/Ssigntool`, so they are **not** signed a second time by the script.

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

1. `build-release.ps1 -Version <v> -PfxPath <pfx>` completes and creates the zip
   and setup `.exe`.
2. `signtool verify /pa /v dist\PrintInterceptor-<v>-setup.exe` and the same for
   `publish\PrintInterceptor.exe` report a valid timestamped signature.
3. Silent install on a clean VM: printer and port exist, `Get-Service
   PrintInterceptor` is running, `PRINTINTERCEPTOR_API_KEY` is set at machine
   scope, and only Administrators/SYSTEM can read `config.json`.
4. Print a POS receipt to the virtual printer: it is fiscalised and printed on the
   real till printer; the tray/jobs window and *Fiscal status* agree with
   FiscalBridge's Fiscal Device page.
5. Tray mode: the Run value starts the app at sign-in for the intended user.
6. Uninstall leaves the service, printer and Run value gone; `-Purge` also removes
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
