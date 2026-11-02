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
        ├─ print   : GDI thermal receipt or A4 invoice (Modern Blue layout)
        └─ report  : the day's fiscal position + the accountant's CSV, from local state
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
| `storage.retentionDays` | How long decoded and raw jobs are kept. The reconcile replay needs this folder; see [Reconciling an unknown receipt](#reconciling-an-unknown-receipt). |
| `reconcile.*` | How the app resolves a fiscalisation whose answer it could not read. See [Reconciling an unknown receipt](#reconciling-an-unknown-receipt). |

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
*Import receipt parse profile…*, *Capture receipt layout from a job…* (below) and
*Open parse profiles folder*.

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

### Capturing a profile from a job the till already caught

A new POS is usually discovered by failure: a job lands in **Needs review** because no rule
describes its layout. *Capture receipt layout from a job…* (tray menu) turns that job into the
profile instead of making someone hand-write one:

- Pick a job from the dropdown — the receipts this till kept, newest first — or **From file…**
  a saved printout. The text is shown exactly as it arrived and stays editable, so a receipt
  from another branch can be pasted in.
- **Analyse** parses the same receipt twice and puts the two readings side by side: what the
  rules in use read now, and what the profile being suggested would read. Every field carries
  the line number it came from, or *— not read —* in red. That preview is the acceptance test;
  nothing is saved on a promise.
- Each rule the profile adds is listed with the values it writes, the line that proves it, and
  why it is needed — "'ITEM' and 'QTY' are the columns your POS prints; the built-in words are
  not on that line".
- **What a profile cannot decide** holds the rest: a tax code printed after an amount that names
  no bucket (a receipt cannot say which VAT bucket `G` means), a word like `STORNO` that would
  change what the document *is*, a printed total announced by words the parser does not read, and
  any suggestion the preview still cannot read. Those are reported for Settings, never written
  into the file — a guess about a credit note files an ordinary sale as a return.

**Save profile** writes it to `%ProgramData%\…\profiles\<id>.json`; **Save and use now** also
applies it and records it in `config.json`, which still needs a restart before it parses.
Because a profile states only differences, the file contains the labels and column words this
POS adds, not a copy of the built-in rules — and a rule whose marker the parser would find on
the wrong line (a footer word that also names a column, like `Total`) is never offered.

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

# The day's fiscal report, from a till that runs headless as a service
.\publish\PrintInterceptor.exe --day-report=2026-10-31 --csv=D:\fiscal\2026-10-31.csv
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
  (only while paused), *Sync offline receipts now*, *Reconcile unknown receipts now*,
  *Fiscal status…*, *Fiscalisation jobs…*, *Daily Z-shift summary…*,
  *Test connection*, *Settings…*, open the config / logs / data / parse-profile folders,
  *Import receipt parse profile…*, *Capture receipt layout from a job…*, *Start with Windows*,
  *Exit*. Day open/close asks for confirmation and reports the gateway's answer; the device state
  is polled every 30 seconds.
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

### Daily Z-shift summary and accountant export

*Daily Z-shift summary…* opens the day's fiscal position as this till knows it: the documents it
fiscalised, their VAT buckets, the receipt numbers they carry, and everything still outstanding.
Pick a business date with the date control (or **Previous day** / **Next day**), then
**Save summary…** for the printable text and **Export lines (CSV)…** for the accountant's file
(`fiscal-day-<date>.txt`, `fiscal-lines-<date>.csv`).

The report reads local state only — the [job ledger](#failure-handling) for the date plus the
printed fiscal copy behind each document (the snapshot in `state/print-snapshots`, or the durable
copy in `fiscalised/<date>/` when the snapshot has been pruned). It never calls the network and
needs no API key, so it works with the line down, and nothing in the window can alter a fiscal
document; it is a statement of what was already issued.

| Section | What it says |
| --- | --- |
| Identity | Company, TIN, VAT number, device id and serial, fiscal day number, the first–last receipt numbers and counters printed that day, and every currency seen. |
| SALES / CREDIT NOTES / DEBIT NOTES | One table per kind: net, tax and gross per VAT bucket, with the document count. Corrections never sit inside the sales buckets. |
| DAY POSITION | Gross sales, the credit and debit notes against them, the day's net and tax, and any gross that is deliberately outside the totals. |
| NOT FISCALISED FOR THIS DATE | Held for review, rejected, outcome unknown, in flight — the documents that are not at the tax authority yet. Offline receipts that are signed and still waiting to sync are counted as issued, and listed separately. |
| NOTES | Every reason a number in this report is smaller than the till roll, in the report itself. |

Honest figures, by construction:

- A document is counted as issued from its ledger entry. If its printed copy is no longer on this
  till, its gross is reported as **Issued, no copy** and its buckets, lines and tax are *not*
  estimated — the notes name those invoices.
- A document whose own tax rows do not add up to its printed total is flagged in the report and
  logged rather than quietly balanced.
- Credit and debit notes are kept in their own kind, and their buckets are signed to agree with
  the document, so a gateway that returns magnitudes cannot make the day's VAT larger than it is.
- A date with more than one currency is announced; the totals are added as printed, never
  converted.

The CSV has one row per printed line of every fiscalised document whose copy is readable here, so
it can be pivoted by bucket, receipt or device without joins:

```text
businessDate, invoiceNumber, transactionType, receiptNumber, receiptCounter, receiptGlobalNo,
fiscalDayNo, fiscalCode, deviceId, deviceSerialNo, currency, signedOffline, buyerName, buyerTin,
line, quantity, unitPrice, lineTotal, taxType, taxRatePercent
```

It is CRLF with a UTF-8 byte-order mark so Excel opens it straight, and any cell that begins with
`=`, `@`, `+` or a control character is prefixed with `'` — a product name typed at the counter
cannot arrive in the spreadsheet as a formula. Exporting a date with no readable lines writes
nothing and says so, instead of producing an empty file that looks like a nil return.

A till that runs only as a Windows service has no tray to open, so the same report is available
from a terminal — the summary goes to stdout, progress and errors to stderr, and the exit code is
`0` (report written), `2` (bad date or path), `3` (no lines to export) or `1` (the file could not
be written):

```powershell
# Today, on screen
.\publish\PrintInterceptor.exe --day-report

# A closed day, with the accountant's export beside it
.\publish\PrintInterceptor.exe --day-report=2026-10-31 --csv=D:\fiscal\2026-10-31.csv

# Redirected straight to a file for the bookkeeper
.\publish\PrintInterceptor.exe --day-report=2026-10-31 > D:\fiscal\day-2026-10-31.txt
```

Run it before **Close fiscal day**: if the notes list a job whose outcome is unknown, run
*Reconcile unknown receipts now* first, so the day is reported closed on figures the till can
actually read.

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

## Returns, refunds and corrections

A return is the one document that can quietly file VAT on money that went back to the
customer, so the parser decides *what the document is* before anything is sent. The gateway
signs a correction from `transactionType` — it takes positive `quantity` and `unitPrice` on
every line and negates the totals itself for a credit note — and it answers `400` for a
correction that carries no `relatedInvoiceNumber`, because ZIMRA has no other way to tie the
two documents together.

Three signals type a document, in the order the paper actually states them:

1. **A word above the items** — `parse.corrections.creditNoteKeywords` / `debitNoteKeywords`,
   matched as whole words. `RETURN`, `REFUND`, `REVERSAL`, `CREDIT NOTE` …
2. **Its own number** — `parse.corrections.creditNoteNumberPrefixes` (`CR`, `CN` by default)
   types a slip the POS numbers as a correction.
3. **A negative total** — `parse.corrections.negativeTotalMeansCreditNote` (default `true`)
   files a slip whose printed amounts are negative as a credit note and warns that the
   wording is missing, which is the hint to add it to the profile.

| Situation | Result |
| --- | --- |
| A correction with no original invoice number printed | `NeedsReview` — nothing is sent, and the reason names `parse.corrections.originalInvoiceLabels` because a missing reference is a profile gap |
| A correction with no printed reason | Fiscalised with a generic reason and a warning; the gateway demands the field |
| A slip mixing a sale line and a negative line | `NeedsReview` — the gateway totals from positive prices, so one of the two would be filed wrongly. The POS has to print the return as its own credit note |
| Every line negative | `CreditNote`, in its own signed total |
| `NO RETURNS AFTER 7 DAYS` in the footer | Still a sale: only the part printed above the items announces a document type, and `RETURNS` is not the word `RETURN` anyway |
| An item called `Brass Return Coupling` | Still a sale — same reason |
| The credit note prints the original's number as its own | `parse.corrections.originalInvoiceLabels` lines are excluded from the number search, so the correction is never filed under the sale it reverses |
| A reprint of a fiscalised sale that is actually a return | The ledger keys duplicate protection on invoice number **and** document type, so a credit note on a used number is not suppressed as a duplicate print |

The vocabulary is data, so a new POS is still a profile: `parse.corrections` travels with a
captured profile like the rest of the layout rules. A keyword list written as `[]` means
"this POS never prints the word" and the type then comes from the number or the sign alone.
A keyword with no letters, a prefix shorter than two letters (it would type ordinary invoice
numbers), a word listed as both credit and debit, or a blank label are rejected at load and
import. Line guards are checked on magnitudes, so a `-1.00M-100.00` row is rejected for the
same arithmetic its positive twin passes.

## Duplicate protection

Every `POST /fiscalize` creates a real ZIMRA receipt, so capture is deliberately strict:

1. Before fiscalising, the job is **claimed** in a durable ledger (`InFlight`) written with
   an atomic replace. If the process dies mid-request, the entry stays blocking rather than
   silently retrying.
2. A job is skipped when its **text hash** was already processed, or when the same
   **invoice number + business date + document type** was already fiscalised. POS invoice
   numbers reset daily, so the date is part of the key, and a credit note that reuses the
   original's number is a different document rather than a second print. An entry written
   before the distinction existed blocks every type.
3. `InFlight`, `Fiscalised`, `FiscalisedOffline` and `AmbiguousFailure` entries block
   re-fiscalisation. `Rejected` and `NeedsReview` do not.
4. A receipt signed offline is also blocked by its queue file: it stays claimed until the
   sync succeeds or the receipt is rejected and moved to `offline-failed`.

## Failure handling

| Situation | Result |
| --- | --- |
| No line items, totals mismatch, a parsed total of zero, no API key | `NeedsReview` — nothing is sent |
| A correction with no original invoice reference, or a slip that mixes a sale with a return | `NeedsReview` — the parser holds it before anything is sent, so nothing has to be undone once the document is fixed |
| API returns 4xx or `success=false` | `Rejected` — job is not retried automatically |
| Timeout, connection failure, 5xx, unreadable 2xx | `AmbiguousFailure` — **never retried automatically**, because the receipt may already exist at ZIMRA. [Reconciliation](#reconciling-an-unknown-receipt) settles it |
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

## Reconciling an unknown receipt

A timeout, a dropped connection or a `5xx` says only that the till never got an answer. The
sale may already sit in ZIMRA with a receipt number against it, which is why such a job is
never retried on its own: a second `POST /fiscalize` would be a second fiscal document for
one basket of goods. Reconciliation is the pass that finds out which it was, and it is the
only route by which an `AmbiguousFailure` job stops blocking its invoice number.

It reads the tenant's own invoice list — `GET /api/v1/invoices?page=N&limit=100`, newest
first — because that is the answer the gateway can give for a receipt the till never got an
id for, and it acts on three outcomes:

| What the server says | What happens |
| --- | --- |
| The receipt is there, and its total matches the parsed receipt | The gateway's proof is **adopted**: the job becomes `Fiscalised` with the server's fiscal code, QR data, receipt and day numbers, and the fiscal copy the customer never got is printed. |
| The receipt is not there | The **exact body that timed out** is replayed, read back from `jobs/<date>/<hash>.request.json` and sent byte for byte, so the gateway sees the same invoice number, lines and totals it was already being shown. A success marks the job fiscalised and prints. |
| The replay is refused `409 DUPLICATE_INVOICE` | The receipt landed after the list was read, so the list is read again and the proof adopted — not reported as a rejection. |

The rest of the table is what reconcile refuses to do, and each refusal exists because the
alternative is a fiscal problem nobody can undo:

- **A list that cannot be read is not an answer.** A `5xx`, a non-JSON body, or a page the
  client cannot parse leaves the job blocked. Nothing is sent on an answer that means nothing.
- **A total that does not match is not adopted.** The invoice number is the only thing looked
  up, so a match within 5 cent is required before someone else's receipt is written into the
  ledger and printed on this till. A mismatch becomes `NeedsReview`.
- **A job whose receipt text shows no invoice number is never replayed.** The gateway
  auto-numbers a request that carries none, so a replay would create a second sale under a
  fresh number that its own duplicate check cannot see. It stays blocked and is logged for the
  dashboard.
- **Nothing is unblocked by an exhausted scan.** `scanPages` bounds how far back the newest-first
  walk goes; a receipt older than the window is reported as such rather than assumed absent.

| Key | Default | Purpose |
| --- | --- | --- |
| `reconcile.enabled` | `true` | Runs the automatic passes; *Reconcile unknown receipts now* works regardless. |
| `reconcile.intervalMinutes` | `15` | Minutes between background passes. `0` means startup and the tray only. |
| `reconcile.autoResend` | `true` | Replay a receipt the server has never heard of. `false` = only adopt the server's proof, never send. |
| `reconcile.maxAttempts` | `3` | Passes per job before it is left for the operator. The tray action ignores the budget. |
| `reconcile.scanPages` | `10` | Pages of 100 invoices the lookup reads, newest first. |

A pass runs at startup, then every `intervalMinutes`, and on demand from the tray; one pass at
a time, so a slow server cannot stack them. Adopted proofs print without a verification code
because the list route does not carry one — the fiscal code, QR data and receipt numbers on the
paper are the gateway's own, and the full document is in FiscalStack. Reconciliation is also
honest when retention is off: with `reconcile.autoResend` `false` (or `storage.retainJobs`
`false`, which the config rejects against `autoResend` for good reason) the lookup still
settles receipts that exist, and a receipt the server does not have is set aside as
`NeedsReview` naming `storage.retainJobs` rather than silently dropped.

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
