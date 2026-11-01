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
        ├─ parse   : profile-driven labels + qty × price ≈ amount column detection
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
  (`Generic / Text Only` needs no Ghostscript). Either build the package with
  `-GhostscriptDir` so it ships inside the release, or install 64-bit Ghostscript on the
  till and add `gswin64c.exe` to `PATH` / set `ghostscriptPath`. The app checks the config
  value, then `<exe folder>\ghostscript\bin`, then `PATH`, then `Program Files\gs`.

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
| `printing.template` | `fiscal` (this app's layout) or `original` (the POS's own document with the fiscal proof stamped on it). See [Print the POS's own invoice](#print-the-poss-s-own-invoice). |
| `printing.accentColor` | Accent used by the fiscal layouts (rules, table header, totals). Defaults to `#4F46E5`, the indigo of the sample invoice. |
| `printing.enabled` | `false` fiscalises without printing (use only knowingly). |
| `ghostscriptPath` | `gswin64c.exe` path when PostScript/PDF jobs are captured. Leave empty for a bundled or PATH install. |
| `company` | Printed on the fiscal document. `email` and `logoPath` are used by the A4 layout; `logoPath` is relative to the config folder. |
| `parse.*` | The layout rules for this POS: markers, labels, keywords, date formats, line patterns and tolerances. See [Parsing profiles](#parsing-profiles). |
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

## Parsing profiles

Every rule the parser uses to *find* text — section markers, field labels, non-item
keywords, date formats, the collapsed-invoice-table header words, the line-shape regular
expressions and the numeric tolerances — lives under `parse.*`. A **profile** is that rule
set as a named JSON file, so supporting a new POS is importing a file instead of shipping a
build. Two ship in `{app}\profiles`:

| Profile | Contents |
| --- | --- |
| `sage` | Every rule stated in full. This is the layout the parser was written for, so it doubles as the template to copy. |
| `zohobooks` | Only what differs: `NUMBER:` instead of `Order No.:`, `Customer VAT No`, `Invoice Date`, and a `Notes:` line that ends the item table. |

A profile states only the lines it changes; everything it leaves out keeps the built-in
value. Profiles are read from two folders, and an installed file replaces the shipped one of
the same name:

| Folder | Contents |
| --- | --- |
| `{app}\profiles` | Shipped with the release. Read-only. |
| `%ProgramData%\FiscalStack\PrintInterceptor\profiles` | Imported and captured profiles. |

In **Settings → Receipt parsing profile**: pick one and press **Use this profile**,
**Import…** a file received from another till, or **Save current rules as…** to capture what
this till is reading correctly today and hand it to the next one. The tray menu has
*Import receipt parse profile…* and *Open parse profiles folder*.

Applying writes the resolved rules into `config.json` and records the name in `parse.profile`
as provenance. The configuration stays the single source of truth, so deleting a profile file
cannot leave a till unable to read its receipts, and rules tuned by hand survive a restart — a
profile is only ever applied when an operator presses the button. The parser reads the rules
when it starts, so an applied profile takes effect after PrintInterceptor (or the service)
restarts.

**What a profile cannot change.** A profile decides where text is found and how a line
declares its VAT status, never what must add
up. `qty × price = amount`, agreement with the printed total, and the column scoring stay in
code, and `vatRate`, `vatFlag`, `currencies`, `paymentMethod` and the `taxSymbol` /
`nonTaxSymbol` knobs are deliberately not profile
fields — importing a layout can never move the numbers a receipt is fiscalised with. An
import is rejected before it lands if a pattern is missing a group the parser reads (`value`,
`name`, `qty`, `price`, `disc`, `vat`, `exclusive`, `amount`), does not compile, uses a
`dateFormats` entry with no date tokens, sets a tolerance outside its sane range, names a tax
type other than `STANDARD`, `ZERO_RATED` or `EXEMPT`, or uses a
name that could escape the profiles folder. Every pattern also runs under
`parse.limits.regexTimeoutMilliseconds` (1000 by default): a hand-written expression that
backtracks forever holds that one job for review instead of stalling the till.

### VAT status on the line

Which bucket a line is filed under — `STANDARD`, `ZERO_RATED` or `EXEMPT` — is the only tax
decision the till makes: the gateway maps it to a ZIMRA tax ID and charges the company rate
behind the API key, so the rate itself is not read from the receipt and never travels with a
profile. What does travel is the vocabulary for *reading* the bucket off the paper, because
every POS prints it differently. A marker is honoured in three windows only:

| Window | Where a marker may match | Example line |
| --- | --- | --- |
| `slot` | the tokens after the amount, or a code glued to it | `Bread Loaf 1.00 2.50 2.50 EX` |
| `annotation` | text inside `()`, `[]`, `{}` or `<>` | `Statutory Levy (EXEMPT) 1.00 5.00 5.00` |
| `section` | a header line, which names the item lines below it | `ZERO RATED` above the next items |

`parse.defaultTaxType` (default `STANDARD`) files a line that declares nothing, and
`parse.taxRules` is the ordered vocabulary:

```json
"taxRules": [
  { "taxType": "EXEMPT", "markers": ["EXEMPT", "EX", "NO VAT", "NON-VAT", "C"],
    "where": ["slot", "annotation", "section"] },
  { "taxType": "ZERO_RATED", "markers": ["ZERO", "ZERO RATED", "ZR", "B"],
    "where": ["slot", "annotation", "section"] },
  { "taxType": "STANDARD", "markers": ["VAT", "STANDARD", "STD", "A"],
    "where": ["slot", "annotation"] }
]
```

The A/B/C letters are the tax codes a ZIMRA till prints, the same ones `offline.taxes`
provisions. The longest marker on a line wins — `NON-VAT` is a better statement than the `VAT`
inside it — and rule order only breaks ties. A code that shifts the columns, like the `EX`
above, is split off before the columns are detected, so `qty × price = amount` still lines up.

Three deliberate limits:

- **The product description is never read.** `Zero Sugar Coke` and `Exempt Levy Water` stay
  `STANDARD`: what the customer bought is not a tax statement. This is also a change for
  `taxSymbol` / `nonTaxSymbol`, which used to be looked for anywhere in the name — they are
  now slot/annotation markers at the lowest precedence, and stay out of profiles.
- **A sale is never held over tax.** Two markers on one line, or none at all, is a warning on
  the job and the receipt still fiscalises. Ambiguity is a profile-editing task, not a queue.
  A receipt where *some* lines declared their status and others did not is the case worth
  reading the warning for: that is what a missing code looks like.
- **A 0% figure is not a claim of exemption.** An invoice row printing `0.00%` is read as
  `ZERO_RATED` and warned about, because zero-rated and exempt are different ZIMRA buckets and
  only the POS's own code tells them apart. If the POS prints one, capture it with the
  optional `tax` group at the end of `parse.patterns.invoiceRow` — it is matched as a `slot`
  marker, and that is how an exempt supply stops being filed as a zero-rated one.

A marker made only of figures (`0%`, `15`) is rejected at import: the parser reads those
tokens as amounts, so such a rule would silently do nothing.

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
  *Test connection*, *Settings…*, open the config / logs / data / parse-profile folders,
  *Import receipt parse profile…*, *Start with Windows*, *Exit*. Day open/close asks for
  confirmation and reports the gateway's answer; the device state is polled every 30 seconds.
- **Notifications** (balloons on transition, not repeatedly): a fiscal day close failed, the
  offline queue paused, or new receipts were held for review. The tray icon alone is easy to
  miss on a busy till.
- **Settings** edits the endpoint, API key, print format and printers, paper width, the
  [parsing profile](#parsing-profiles) and the offline switch, then writes `config.json`.
  **Refresh from FiscalStack** provisions the ZIMRA
  device, key, tax buckets and counters and shows the result (device, serial, fiscal day,
  counters, key source). Printing changes apply on the next receipt; restart the app after
  changing the endpoint, API key, or offline signing, and after applying a parsing profile.
  The private key itself is cached under
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

The A4 layout is a GDI port of FiscalBridge's own invoice template
(`client/src/components/invoices/pdf-document.tsx`), so the printed page and the PDF the POS
offers for download are the same document: 24 pt page padding, logo left / verification code
centre / ZIMRA QR right above a hairline rule, title with the accent bar and the
`ZIMRA fiscal document` strap, 48 % seller and buyer cards, the fiscal grid with its 3 pt
accent spine (invoice number = the fiscal receipt number `counter/global`, the POS's own
number on the customer reference line), an accent-headed item table with VAT per line and
zebra rows, tax analysis beside the totals card, notes and footer. Long invoices paginate
with a repeated header.
Every spacing, font size and colour comes from that stylesheet, and 1 pt values are converted
to the driver's 1/100-inch units; Arial stands in for the embedded Helvetica because it is the
metric twin that is guaranteed to exist on Windows. It is drawn directly through the Windows
printer driver, so it prints silently from the service — no browser, WebView or PDF toolchain
is required, and no PDF file is produced.
`parse.vatFlag = "0"` switches the table to exclusive-VAT columns (`Amount (excl.)`).

The captured POS text is the source of the line items; the API response supplies totals,
taxes and fiscal fields. Buyer name/TIN/VAT prefer the registered buyer returned by
FiscalStack and fall back to what the POS printed.

## Print the POS's own invoice

`printing.template` decides what the customer is handed:

| Value | Behaviour |
| --- | --- |
| `fiscal` (default) | This app's own receipt/invoice layout, rendered from the fiscal response. |
| `original` | The POS's captured job goes to paper unchanged and the fiscal proof is stamped onto it. |

With `original` there is no second template to keep in step with the POS — the shop's own
document carries the ZIMRA proof:

- **Thermal** — the captured bytes are replayed to `printing.printerName` as a RAW spool job,
  so the till prints exactly what the POS told it to (its own code page, density and
  formatting), and the fiscal stamp is printed straight after on the same roll.
- **A4** — the job is rasterised with Ghostscript at 150 dpi and printed through the Windows
  driver, with the stamp drawn under the last page's image. The page is scaled down when the
  POS filled it to the bottom, so the proof is never cut off.

The stamp is the same block on both, and it carries only what the POS could not know when it
printed the document — the six ZIMRA identifiers, in the same order the invoice's own fiscal
grid uses them:

| Label | Value |
| --- | --- |
| `Invoice No` | the fiscal receipt number as `day-counter/global-number`; falls back to the top-level receipt number FiscalStack returns, then to the POS's number |
| `Date` | issue date, without the time |
| `Customer Reference No` | the POS's own invoice number, e.g. `INV-002` |
| `Fiscal Device ID` | tenant device the receipt was signed under |
| `Fiscal Day No` | open fiscal day |
| `Device Serial No` | provisioned device serial |

The QR code and the verification URL print under those lines. Totals are not repeated — they
are already on the invoice the stamp sits on. If the response carried no QR, the fiscal code
is printed as text so the proof stays verifiable by hand. An operator reprint is headed
`FISCAL PROOF - DUPLICATE COPY`. The fiscal templates use the same invoice-number rule: the
receipt number ZIMRA quotes is the invoice number, and the POS's own number is the customer
reference.

`original` requires `storage.retainJobs = true`: the stamp is printed from the bytes saved
under `data/jobs/<date>/original/<hash>.{pdf,ps,bin}`, and the path is stored in the job's
print snapshot so a reprint finds its own document again. If the fiscal document is already
issued but the stamped copy cannot be produced — retention was off, the file aged out, or the
replay/raster failed — the job falls back to the `fiscal` template and the log says so, rather
than leaving the customer with nothing. `config.json` validation rejects
`"template": "original"` together with `"retainJobs": false` so this cannot be set up silently.

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

## The fiscalisation request

`POST {apiEndpoint}fiscalize` with `X-API-Key` is a pass-through: the key is the tenant, so
no company id is sent, and totals, tax ids, HS codes, counters and hashes are computed
server-side. The route validates the body with a strict schema, which decides two rules for
everything built from receipt text — a field is sent as one of its enum values or left off,
and a field with no value is absent rather than `null`, because an explicit `null` is a
validation error that rejects the whole sale.

| Field | Sent | When the till text does not map |
| --- | --- | --- |
| `date` | `yyyy-MM-dd` from the printed date | Omitted; the gateway uses its own day |
| `currency` | `USD`, `ZWG` for ZWL/RTGS, `LSL` for loti | Omitted; the gateway issues the receipt in the company's own currency |
| `paymentMethod` | `CASH` / `CARD` / `MOBILE` / `TRANSFER` | Omitted; defaults to `CASH` |
| `transactionType` | `FiscalInvoice` / `CreditNote` / `DebitNote` | Omitted; defaults to `FiscalInvoice` |
| `items[].taxType` | `STANDARD` / `ZERO_RATED` / `EXEMPT` | Omitted; defaults to `STANDARD` |
| `items[].quantity`, `unitPrice` | Absolute values | The gateway signs corrections from `transactionType` and rejects a negative price |
| `buyer` | Only when a TIN of exactly 10 digits was printed | Left off entirely — ZIMRA receives a buyer against a TIN, and a name-only block is rejected. A VAT number that is not 9 digits, an email the gateway cannot parse, or a phone over 20 characters is dropped on its own |
| `invoiceNumber` | Always, when the POS printed one, so a blind retry returns `409 DUPLICATE_INVOICE` instead of minting a second receipt | Omitted when no number was printed; the gateway generates one |

A rejection is reported with the gateway's error code, its per-field complaints and its hint,
so `Rejected` in the jobs list says which line to fix (`VALIDATION_ERROR: Invalid request
body [paymentMethod: Invalid enum value. Expected 'CASH' | 'CARD' | 'MOBILE' | 'TRANSFER', received 'PayNow'] — …`).

The `offline*` fields on a queued receipt are ours, not the schema's: the gateway ignores
them and assigns the next ZIMRA counters when it processes the sync.

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
| No line items, totals mismatch, non-positive parsed total, no API key | `NeedsReview` — nothing is sent |
| Credit note or debit note without an original invoice reference | `Rejected` by the gateway's early field check; the correction is never stored, so it can be re-sent once the reference is on the document |
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
- The pass-through route takes any ISO 4217 code and defaults to the company's own
  currency, so an RSL till whose receipts print LSL is fiscalised in loti. Currency is
  sent only when the till text names a code the factory recognises; anything else is
  left off rather than guessed.
- Invoice numbers come from the receipt text. If the POS does not print one, dedup falls
  back to the text hash; configure `parse.invoiceNumber` where possible.
- A [parsing profile](#parsing-profiles) changes only where text is found: a layout whose
  lines do not add up is still held for review, not fiscalised from a best guess. Applying
  one takes effect at the next restart, and the profile controls in Settings are WinForms
  code authored on a Linux host — click through *Use this profile*, *Import…* and
  *Save current rules as…* once on a Windows till.
- Buyer details reach ZIMRA only when the receipt text carried a 10-digit TIN; identifiers the
  gateway's schema rejects are dropped instead of sent, so a malformed VAT number disappears
  from the fiscal receipt rather than failing the sale. See
  [The fiscalisation request](#the-fiscalisation-request).
- One instance captures one printer/tenant/device. Run one service per till.
- Tested for parsing and ledger behaviour with `dotnet test`. The Windows printer driver,
  Ghostscript invocation and service install must be verified on a Windows till.
- The packaged installer and Authenticode signing scripts are authored for Windows and
  have not been executed on the Linux build host. Run the checklist in
  [`packaging/README.md`](packaging/README.md) once per release on a Windows agent.
- `printing.template = "original"` replays the POS's own bytes; it cannot re-render them. A
  till whose driver rejects a RAW job, or an A4 job Ghostscript cannot rasterise, falls back
  to the fiscal template, so the proof is always printed even when the shop's layout is not.
- A4 invoices are rendered with GDI, not from FiscalBridge's HTML. The geometry is taken from
  the invoice template's stylesheet, but the driver substitutes its own metrics for Arial and
  rounds to its own dot grid, so a printed page can differ from the PDF by a fraction of a
  millimetre. No PDF copy is saved; FiscalStack remains the source for archived fiscal
  documents.

## Licensing note

Ghostscript is AGPL-licensed. The release can ship it inside the package
(`packaging/build-release.ps1 -GhostscriptDir`); doing so obliges you to carry its licence
text and the corresponding source, or to hold a commercial licence from Artifex. Review
that before producing a bundled package — installing Ghostscript on the till separately
avoids the question, and the `Generic / Text Only` capture path does not need Ghostscript
at all.
