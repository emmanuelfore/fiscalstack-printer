# PrintInterceptor

Windows service that captures POS receipts at the printer level, fiscalises them through
FiscalStack, and prints the ZIMRA fiscal receipt. It is a standalone sibling of
[`FiscalBridge`](../FiscalBridge) and does not read or modify anything in it.

The POS keeps printing to a **virtual printer** instead of a folder. PrintInterceptor
listens on a raw TCP port, receives the job, extracts text, parses the line items, calls
`POST /api/v1/fiscalize`, and then prints a receipt containing the QR code, verification
code, fiscal code and receipt counters.

When the API is unreachable it can instead **sign the receipt on the till** with the ZIMRA
device key, print it, and sync the queued receipt once the connection returns. It runs
either as a Windows service or as a **tray application** with a status icon and settings.

```
POS ──► "FiscalStack Fiscal Printer" (virtual, raw TCP 127.0.0.1:9100)
              │
              ▼
      PrintInterceptor service / tray app
        ├─ decode  : raw text, or PostScript/PDF via Ghostscript txtwrite
        ├─ parse   : markers + qty × price ≈ amount column detection
        ├─ guard   : duplicates, totals, empty receipts → hold, never fiscalise
        ├─ fiscalise: POST /api/v1/fiscalize (X-API-Key)
        │            └─ unreachable + offline.enabled → sign, queue, print
        ├─ sync    : queued signatures oldest-first when the API returns
        └─ print   : GDI thermal receipt or A4 invoice (Modern Blue layout)
```

## Requirements

- Windows 10/11 or Windows Server 2019+ (the capture, printing and service code is Windows-only).
- .NET 8 SDK to build; the published self-contained executable needs no runtime.
- A FiscalStack API key and the company already activated on ZIMRA.
- For offline signing only: a reachable FiscalStack once, so the device key, tax mapping and
  counters can be provisioned and cached. Nothing else is configured locally.
- **Ghostscript** only if the virtual printer uses a PostScript or PDF driver
  (`Generic / Text Only` needs no Ghostscript). Install 64-bit Ghostscript and either add
  `gswin64c.exe` to `PATH` or set `ghostscriptPath` in the config.

## Build

```powershell
dotnet publish src\PrintInterceptor -c Release -r win-x64 --self-contained true -o publish
```

On Linux/macOS the Core library and tests build, but the Windows host cannot run:

```bash
dotnet test tests/PrintInterceptor.Core.Tests -c Release
dotnet build src/PrintInterceptor -c Release     # compiles, cannot execute
```

## Packaged installer

For till deployment use [`packaging/build-release.ps1`](packaging/build-release.ps1) on a
Windows build host. It publishes self-contained `win-x64`, optionally signs every
executable with Authenticode (SHA-256 plus RFC 3161 timestamp), and produces both a zip
and an Inno Setup `.exe` that installs the virtual printer, config, ACLs and either the
service or tray autostart:

```powershell
.\packaging\build-release.ps1 -Version 1.2.0 -PfxPath C:\keys\fiscalstack.pfx
```

The setup is unattended-capable (`/VERYSILENT /TASKS="printer"`), keeps the API key out
of `config.json` (machine environment instead), preserves fiscal data on uninstall, and
avoids the installer being signed twice. See [`packaging/README.md`](packaging/README.md)
for prerequisites, signing options, direct `install.ps1` usage, the Ghostscript AGPL
caveat, and the Windows-only verification checklist.

## 1. Install the virtual printer

Run PowerShell **as Administrator**:

```powershell
# Raw text jobs (Generic / Text Only driver) - simplest, no Ghostscript
.\addprinter.ps1 -Mode Text

# Or PostScript/PDF jobs (Microsoft PS Class Driver) - requires Ghostscript
.\addprinter.ps1 -Mode PostScript
```

This creates the raw TCP port `FiscalStack_Raw_9100` and the printer
**“FiscalStack Fiscal Printer”**. Point the POS at that printer.

- Do **not** set it as the Windows default printer — the fiscal receipt must go to the real
  till printer.
- Remove it with `.\addprinter.ps1 -Remove`.

## 2. Configure

The service creates a default config on first run:

```
%ProgramData%\FiscalStack\PrintInterceptor\config.json
```

Use [`config.example.json`](config.example.json) as the reference. The fields that matter
most:

| Key | Purpose |
| --- | --- |
| `listenPort` | Must match the virtual printer port (default `9100`). |
| `apiEndpoint`, `apiKey` | FiscalStack tenant and pass-through key. Without a key, jobs are held for review. |
| `printing.printerName` | Real till printer for the thermal receipt. Empty = Windows default. |
| `printing.format` | `thermal`, `a4`, or `auto` (A4 when the captured job was a PDF, thermal otherwise). See [A4 invoices](#a4-invoices). |
| `printing.a4PrinterName` | A4/laser printer for invoice output. Empty = `printing.printerName`, then the Windows default. |
| `printing.paperWidthMm` | `58` or `80` for thermal output. |
| `printing.enabled` | `false` fiscalises without printing (use only knowingly). |
| `ghostscriptPath` | `gswin64c.exe` path when PostScript/PDF jobs are captured. |
| `company` | Printed on the fiscal document. `email` and `logoPath` are used by the A4 layout; `logoPath` is relative to the config folder. |
| `parse.*` | Markers and metadata keywords for this POS layout. |
| `offline.enabled` | Sign receipts on the till when the API is unreachable. Everything it needs (device, key, taxes, counters) is provisioned from FiscalStack with the API key; see [Offline signing](#offline-signing). |
| `storage.retentionDays` | How long decoded and raw jobs are kept. |

`parse.vatFlag` follows FiscalStack convention: `"1"` means printed prices include VAT,
`"0"` means VAT is added on top. `parse.productStartLine` / `productEndLine` support
`Order No.` / `Items count`-style markers; when empty, the parser detects the item block
automatically. Receipts that wrap an item over two lines (`34400 x 2.400`, then
`NAME *82,560.00`) are parsed as a fallback when no single-line columns balance.

`apiEndpoint` is the base URL. `/fiscalize` is appended by the client:

```
https://fiscalstack.co.zw/api/v1/    →  POST https://fiscalstack.co.zw/api/v1/fiscalize
```

The key is sent as the `X-API-Key` header. Generate it in FiscalStack under the company's
API settings (`POST /api/companies/:id/api-keys/generate`, owner only). It is shown once and
starts with `sk_test_` or `sk_live_`; paste it into `apiKey`. To keep the secret out of
`config.json`, set the `PRINTINTERCEPTOR_API_KEY` environment variable instead — it
overrides the file at startup. Restrict the config file's ACL to Administrators and SYSTEM,
since the service runs as LocalSystem.

Other environment overrides:

| Variable | Effect |
| --- | --- |
| `PRINTINTERCEPTOR_CONFIG` | Full path to `config.json` (same as `--config`). |
| `PRINTINTERCEPTOR_API_KEY` | Replaces `apiKey` from the file. |

## 3. Run

```powershell
# Foreground console (logs to stdout and the log file)
.\publish\PrintInterceptor.exe --console

# Custom config path
.\publish\PrintInterceptor.exe --console --config C:\path\config.json
```

Configuration can also be selected with the `PRINTINTERCEPTOR_CONFIG` environment variable.
The process exits non-zero when the config cannot be read or fails validation.

### Tray mode

```powershell
.\publish\PrintInterceptor.exe --tray
```

Tray mode runs the listener, the pipeline and the offline sync loop in a normal desktop
process and adds a notification-area icon. Use it when the till should show fiscalisation
status without a Windows service. A service cannot host a tray icon (it runs in session 0),
so choose one mode per till.

- **Icon:** green = API online and queue empty; amber = API online with receipts waiting to
  sync; red = API unreachable, status unknown, or the sync queue is paused. The tooltip shows
  the API state, fiscal day, queued receipts and any receipts held for review.
- **Menu:** live API status, fiscal day number/state, *Open fiscal day…* / *Close fiscal day…*
  (enabled from the live state), queue size, receipts needing review, *Resume offline sync*
  (only while paused), *Sync offline receipts now*, *Fiscal status…*, *Fiscalisation jobs…*,
  *Test connection*, *Settings…*, open the config / logs / data folders,
  *Start with Windows*, *Exit*. Day open/close asks for confirmation and reports the gateway's
  answer; the device state is polled every 30 seconds.
- **Notifications** (balloons on transition, not repeatedly): a fiscal day close failed, the
  offline queue paused, or new receipts were held for review. The tray icon alone is easy to
  miss on a busy till.
- **Settings** edits the endpoint, API key, print format and printers, paper width and the
  offline switch, then writes `config.json`. **Refresh from FiscalStack** provisions the ZIMRA
  device, key, tax buckets and counters and shows the result (device, serial, fiscal day,
  counters, key source). Printing changes apply on the next receipt; restart the app after
  changing the endpoint, API key, or offline signing. The private key itself is cached under
  `data/state`, never written to `config.json`.
- **Start with Windows** writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, so it
  applies to the user who ticks it, not to all users.

Double-click the icon to open settings. There is no main window; the app keeps running in
the notification area until *Exit* is chosen.

### Fiscal status and day control

*Fiscal status…* opens the same view as FiscalBridge's **Fiscal Device** page. Everything is
resolved from the API key — no company or device id is entered on the till:

| Tab | Source | Shows |
| --- | --- | --- |
| Status | `GET /api/zimra/device-status` | Reachability, device id, serial, fiscal day number, open/closed/closure-pending state, last global receipt, day-open time and any close error code. It also lists this till's capture port, receipt printer, print format and API endpoint, so the window is self-identifying on a multi-till site. |
| Card details | `GET /api/zimra/device-details` | The ZIMRA/RSL registration card: TIN, VAT, BPN, company name, address, registration and serial numbers, logo URL. |

One instance serves one capture printer, one tenant and one fiscal device, so this window is
always the device behind the printer that till is sending to. Day open/close goes to the
fiscal gateway for that device (the same one FiscalBridge shows), not to the Windows printer;
fiscal receipts themselves still print on the configured receipt printer.

While open, the window refreshes status and card details every 20 seconds, so a day opened or
closed from FiscalBridge (or a close the gateway rejects) appears without pressing *Refresh*.
The header shows the provisioned company identity (see
[company identity](#company-identity)) and its logo, so the operator can confirm the till is
printing for the right tenant.

**Open fiscal day** calls `POST /api/zimra/day/open`; **Close fiscal day** calls
`POST /api/zimra/day/close` and waits for the server to confirm the closure with the gateway
(up to ~90 seconds). Both routes resolve ZIMRA or LEKUKA from the company record, so the same
screen works for both deployments. A day can only be opened when none is open, and a close
that fails leaves the day open with its error code visible so it can be retried:

- `MissingReceipts` — receipts are missing from the day's sequence (grey receipts).
- `ReceiptsWithValidationErrors` — the day contains rejected receipts (red receipts).
- `BadCertificateSignature` / `CountersMismatch` — the gateway rejected the device signature or totals.

Fix the underlying receipts in FiscalStack before retrying; the print queue cannot fix a
rejected fiscal day on its own.

## Company identity

The printed name, TIN, VAT number, address, phone and logo come from FiscalStack. The desktop
fetches `GET /api/v1/company` with the API key at startup and every six hours, caches it in
`data/state/company-profile.json`, and downloads the logo to
`data/state/company-logo.{png,jpg,gif,bmp}` so printing works without a network. Any field the
server does not return (or before the first successful fetch) falls back to the `company`
block in `config.json`; a failed refresh never clears the cached identity. This is why the
per-till `company` block only needs values for a till that has never reached FiscalStack.

## A4 invoices

`printing.format` selects the fiscal output:

| Value | Behaviour |
| --- | --- |
| `thermal` (default) | 58/80 mm receipt on `printing.printerName`, as before. |
| `a4` | Full-page invoice on `printing.a4PrinterName`, with `printing.printerName` as fallback. |
| `auto` | A4 when the captured print job was a PDF, thermal otherwise. Useful when the POS prints invoices as PDF and receipts as plain text. |

The A4 layout mirrors FiscalBridge's **Modern Blue** template: header with the company logo,
verification code + verify URL and the ZIMRA QR; title (`FISCAL TAX INVOICE`, `CREDIT NOTE`,
`DEBIT NOTE`); seller and buyer cards; a fiscal grid with invoice/counter, fiscal day, date,
device ID and serial; an accent-headed item table with VAT per line and zebra rows; tax
analysis and totals; notes and footer. Long invoices paginate with a repeated table header.
It is drawn directly through the Windows printer driver (GDI), so it prints silently from
the service — no browser, WebView or PDF toolchain is required, and no PDF file is produced.
`parse.vatFlag = "0"` switches the table to exclusive-VAT columns (`Amount (excl.)`).

The captured POS text is the source of the line items; the API response supplies totals,
taxes and fiscal fields. Buyer name/TIN/VAT prefer the registered buyer returned by
FiscalStack and fall back to what the POS printed.

## 4. Install as a service

```powershell
.\install-service.ps1                      # uses .\publish\PrintInterceptor.exe
.\install-service.ps1 -ConfigPath C:\path\config.json
.\install-service.ps1 -Remove
```

For a complete till install (config, ACLs, virtual printer, service or tray autostart in
one step) use `packaging\install.ps1` or the packaged setup described above.

Logs: `%ProgramData%\FiscalStack\PrintInterceptor\data\logs\printinterceptor-yyyyMMdd.log`.
Captured jobs: `data\jobs\yyyy-MM-dd\`. Durable state:

| Path | Contents |
| --- | --- |
| `data\state\ledger.json` | Every captured job: hash, invoice, status, fiscal response, print result. |
| `data\state\fiscal-chain.json` | Device id, fiscal day, receipt counter, global number, last hash. |
| `data\state\offline-queue\` | One JSON file per signed receipt waiting to sync, oldest-first by filename. |
| `data\state\offline-failed\` | Receipts the server rejected or that need reconciliation; never auto-retried. |
| `data\state\offline-sync-pause.json` | Present while the sync queue is paused after a rejection. Delete only after reconciling, or use *Resume offline sync* in the tray. |
| `data\state\offline-provisioning.json` | Cached device id, key, tax buckets and counters from FiscalStack for offline signing. |

The service runs as LocalSystem, so the configured printer must be visible to that account.

## Offline signing

With `offline.enabled` (after one successful provisioning) the till can issue a real
ZIMRA-shaped receipt without the API:

1. The pipeline first tries `POST /fiscalize` as usual.
2. If the API is **definitely unreachable** (probe gets no HTTP response), the receipt is
   signed with the device key against the durable counter chain, written to the offline
   queue, marked `FiscalisedOffline` in the ledger, and printed with an `OFFLINE` header,
   its counters, verification code and QR code.
3. A background loop (and the tray's *Sync offline receipts now*) pushes queued receipts
   oldest-first. On success the ledger becomes `Fiscalised`, the chain adopts the server's
   counters/hash, and the queue entry is deleted.

A request that times out after being sent is **not** converted into an offline receipt: the
server may already have created one. That stays `AmbiguousFailure`, exactly as online.

### Prerequisites

The till bootstraps itself from the API, like the mobile app: startup calls
`GET /api/zimra/offline-state` with the API key and adopts

- the numeric ZIMRA **device ID** and serial number,
- the device **RSA private key** (`zimraPrivateKey`),
- the live **tax buckets** (`taxType` → `taxID`, `taxCode`, `taxPercent`),
- the **QR base URL**, and
- the **counters and last fiscal hash**.

The key identifies the tenant: `/api/zimra/*` is rewritten to the key's company, so no
company id is configured or sent. The provisioned key and counters are cached in
`data/state/offline-provisioning.json` (owner-only permissions on Unix; restrict the ACL on
Windows), so a restart during an outage can still sign offline. The key is never written to
`config.json`. Press **Refresh from FiscalStack** in tray Settings to provision or re-read
the state; the dialog shows the device, serial, fiscal day, counters, key source and tax
mapping.

- `parse.vatFlag`, currency and the receipt date must be right. Offline receipts embed the
  till's clock (`yyyy-MM-ddTHH:mm:ss`), so a wrong clock produces a wrong receipt date.
  Keep the till time synchronised.

### Counter chain

`fiscal-chain.json` holds the last known `receiptCounter`, `receiptGlobalNo`, `fiscalDayNo`
and hash. Provisioning seeds it from `/zimra/offline-state`; without a reachable API it
advances from the first successful online receipt (`fiscalCode` is the previous hash).
The chain also advances on every online receipt, so offline mode can be switched on later
without another setup step. Nothing is ever rewound: an online success only moves the
counters forward.

Offline signs the **next** number after the last known one (global and daily counter).
The server accepts a queued receipt only when those counters are exactly its next live
ones, which is why the queue is strict:

- Receipts are synced **oldest first**, one at a time.
- A **rejection** moves that receipt to `offline-failed`, marks it `Rejected`, and writes
  `offline-sync-pause.json`; later receipts would carry counters that no longer line up.
  The pause is durable: the 60-second loop and any restart leave the remaining queue alone.
  Reconcile the receipt with FiscalStack, then choose *Resume offline sync* in the tray
  (or delete the pause file deliberately) to continue.
- An **ambiguous** sync (timeout, 5xx, unreadable response) leaves the receipt at the head
  of the queue, marks it `AmbiguousFailure`, and blocks everything behind it. Nothing is
  skipped; the same receipt is retried on the next loop, so verify it in FiscalStack before
  letting it continue.
- While anything is pending, a new sale first attempts a sync. If the queue still cannot
  clear, the new receipt is signed offline too, preserving counter order instead of jumping
  ahead online.

The server re-signs the request when it syncs, and its `fiscalCode` becomes the chain hash
again. The offline signature therefore proves the till signed that receipt, while the
authoritative ZIMRA record is created at sync time.

### Fiscal-day boundary

The counters are only exact while the **same fiscal day stays open**. `closeDay` resets the
daily counter to 1, and the server also closes and reopens a day that has been open longer
than its staleness window (default 24 hours) the next time it fiscalises. A queued receipt
claims `daily counter + 1` for the day it was signed in, so after a close/reopen:

- the **first** queued receipt may still be accepted under the new day, with **new
  counters**, because the server's day-closed path reopens the day and claims fresh numbers;
- **every further** queued receipt is `Rejected` ("daily counter is not the next in
  sequence"), moved to `offline-failed`, and the queue stays paused until it is reconciled.

So the queue must drain **before** the day is closed. In normal operation it does: the sync
loop retries every 60 seconds once the API answers, and a new sale tries to sync before
fiscalising, so an outage that ends during the business day clears on reconnect. An outage
that runs past the close of business — or a `closeDay` issued from another channel while
receipts are queued — cannot be recovered automatically. Those sales must be re-issued from
the POS after reconciling; the printed offline slips have no ZIMRA counterpart until then.

Related: if the server re-signs a synced receipt because its prepared payload differs from
the local one (tax mapping, VAT percent, rounding or clock), the next queued receipt's
signed previous hash no longer matches the live chain and is rejected the same way. Keeping
the till clock correct avoids this.

### What the printed receipt shows

Offline receipts carry the offline counters, the locally computed hash, the verification
code and the ZIMRA QR URL, plus an offline marker. The thermal layout adds an `OFFLINE`
heading and a "queued for ZIMRA sync" line; the A4 layout says "signed offline, queued for
sync" under the title and in the footer. If the server later accepts it, the fiscal proof
in FiscalStack is the definitive one; reprint from there when a customer needs the final
fiscal copy.

## Duplicate protection

Every `POST /fiscalize` creates a real ZIMRA receipt, so capture is deliberately strict:

1. Before fiscalising, the job is **claimed** in a durable ledger (`InFlight`) written with
   an atomic replace. If the process dies mid-request, the entry stays blocking rather than
   silently retrying.
2. A job is skipped when its **text hash** was already processed, or when the same
   **invoice number + business date** was already fiscalised. POS invoice numbers reset
   daily, so the date is part of the key.
3. `InFlight`, `Fiscalised`, `FiscalisedOffline` and `AmbiguousFailure` entries block
   re-fiscalisation. `Rejected` and `NeedsReview` do not.
4. A receipt signed offline is also blocked by its queue file: it stays claimed until the
   sync succeeds or the receipt is rejected and moved to `offline-failed`.

## Failure handling

| Situation | Result |
| --- | --- |
| No line items, totals mismatch, credit note without a reference, no API key | `NeedsReview` — nothing is sent |
| API returns 4xx or `success=false` | `Rejected` — job is not retried automatically |
| Timeout, connection failure, 5xx, unreadable 2xx | `AmbiguousFailure` — **never retried automatically**, because the receipt may already exist at ZIMRA |
| API unreachable and `offline.enabled` | Signed offline, queued and printed; retried only by the sync loop |
| Offline signing requested but key missing/invalid | `NeedsReview`; the reason is logged and shown at startup |
| Offline sync rejected | Receipt moved to `offline-failed`; the queue is paused durably until reconciled and resumed from the tray. Most often the fiscal day closed while it was queued |
| Offline sync timed out / 5xx | Receipt kept at the head, marked `AmbiguousFailure`; later receipts stay blocked and the same one is retried — verify with FiscalStack before letting it continue |
| Fiscalised but printing fails | Ledger says `Printed=Failed`; the receipt is **not** re-fiscalised. Reprint from FiscalStack or check the printer |
| Duplicate invoice/date or identical job | Skipped, with a log line |

A corrupt `ledger.json` or `fiscal-chain.json` stops startup instead of guessing: delete it
only after verifying with FiscalStack that no captured job is unaccounted for. Deleting
`fiscal-chain.json` while offline receipts are queued will break counter matching — sync or
reconcile the queue first.

## Limitations

- Offline mode is scoped to **one open fiscal day**. Queued receipts must sync before
  `closeDay` (or the server's stale-day close, default 24 h), otherwise they are rejected
  and must be re-issued from the POS. See [Fiscal-day boundary](#fiscal-day-boundary).
- Local signatures only match ZIMRA when the tax mapping, VAT percent, total and receipt
  date mirror the server's. The server re-signs at sync; the client signature is proof from
  the till, not the authoritative record.
- Counters cannot be skipped. If the queue is paused by a rejection or an ambiguous sync,
  new receipts keep going offline until the queue is reconciled.
- An offline receipt is issued only when the API is definitely unreachable. A request that
  times out mid-flight stays `AmbiguousFailure` and is not re-issued offline.
- Discounts, rounding lines and non-item charges are not modelled. A receipt whose printed
  total differs from its item total is held for review rather than fiscalised incorrectly.
- Invoice numbers come from the receipt text. If the POS does not print one, dedup falls
  back to the text hash; configure `parse.invoiceNumber` where possible.
- Buyer VAT/TIN are forwarded exactly as printed. No digit-length or country rules are
  applied locally; the API validates them for the tenant's authority.
- One instance captures one printer/tenant/device. Run one service per till.
- Tested for parsing and ledger behaviour with `dotnet test`. The Windows printer driver,
  Ghostscript invocation and service install must be verified on a Windows till.
- The packaged installer and Authenticode signing scripts are authored for Windows and
  have not been executed on the Linux build host. Run the checklist in
  [`packaging/README.md`](packaging/README.md) once per release on a Windows agent.
- A4 invoices are rendered with GDI, not from FiscalBridge's HTML. The layout mirrors the
  Modern Blue template but fonts and spacing can differ slightly by driver, and no PDF
  copy is saved; FiscalStack remains the source for archived fiscal documents.

## Licensing note

Ghostscript is AGPL-licensed. If you distribute this application with Ghostscript bundled,
review the AGPL obligations (or install Ghostscript separately on the till). The
`Generic / Text Only` capture path does not need Ghostscript.
