using System.Text.Json;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor.Core.Pipeline;

public sealed class PrintJobPipeline : IJobProcessor
{
    private readonly InterceptorConfig _config;
    private readonly JobDecoder _decoder;
    private readonly ReceiptParser _parser;
    private readonly FiscalStackClient _client;
    private readonly JobLedger _ledger;
    private readonly JobStore _jobStore;
    private readonly PrintSnapshotStore _snapshots;
    private readonly CompanyProfileService _company;
    private readonly IReceiptPrinter _printer;
    private readonly OfflineReceiptService _offline;
    private readonly OfflineQueue _offlineQueue;
    private readonly OfflineSyncEngine _offlineSync;
    private readonly ILog _log;
    private readonly object _printGate = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    public PrintJobPipeline(
        InterceptorConfig config,
        JobDecoder decoder,
        FiscalStackClient client,
        JobLedger ledger,
        JobStore jobStore,
        PrintSnapshotStore snapshots,
        CompanyProfileService company,
        IReceiptPrinter printer,
        OfflineReceiptService offline,
        OfflineQueue offlineQueue,
        OfflineSyncEngine offlineSync,
        ILog log)
    {
        _config = config;
        _decoder = decoder;
        _client = client;
        _ledger = ledger;
        _jobStore = jobStore;
        _snapshots = snapshots;
        _company = company;
        _printer = printer;
        _offline = offline;
        _offlineQueue = offlineQueue;
        _offlineSync = offlineSync;
        _log = log;
        _parser = new ReceiptParser(config.Parse);
    }

    public async Task ProcessAsync(PrintJob job, CancellationToken cancellationToken)
    {
        var decoded = await _decoder.DecodeAsync(job.Data, cancellationToken);
        if (!decoded.Success)
        {
            var archived = _jobStore.SaveUndecodable(job.Data, decoded.Format);
            _log.Error(
                $"Print job from {job.RemoteEndPoint} could not be read as text: {decoded.Error}" +
                (archived is null ? "" : $" Received file retained at {archived}."));
            return;
        }

        var printFormat = ResolvePrintFormat(decoded.Format);
        var hash = Hashing.Sha256Hex(decoded.Text.Trim());
        var sourceFile = _jobStore.Save(hash, decoded, job.Data);
        var originalJobPath = _config.Printing.Template == "original"
            ? _jobStore.SaveOriginalCopy(hash, job.Data, decoded.Format)
            : null;
        var parse = _parser.Parse(decoded.Text);

        foreach (var warning in parse.Warnings)
        {
            _log.Warn($"Parse warning: {warning}");
        }

        if (parse.Status != ParseStatus.Parsed || parse.Receipt is null)
        {
            var reason = parse.Reason ?? "The receipt could not be parsed.";
            _log.Warn($"Held for review [{Short(hash)}]: {reason}");
            _ledger.RecordNeedsReview(
                hash,
                parse.Receipt?.InvoiceNumber,
                parse.Receipt?.BusinessDateKey ?? DateTimeOffset.Now.ToString("yyyyMMdd"),
                reason,
                sourceFile);
            return;
        }

        var receipt = parse.Receipt;

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            const string reason = "apiKey is not configured; the receipt was not fiscalised.";
            _log.Error(reason);
            _ledger.RecordNeedsReview(hash, receipt.InvoiceNumber, receipt.BusinessDateKey, reason, sourceFile);
            return;
        }

        var duplicateByHash = _ledger.FindBlockingByHash(hash);
        if (duplicateByHash is not null)
        {
            _ledger.RecordDuplicateAttempt(duplicateByHash.Hash, hash);
            return;
        }

        var duplicateByInvoice = _ledger.FindBlockingByInvoice(
            receipt.InvoiceNumber, receipt.BusinessDateKey, receipt.TransactionType);
        if (duplicateByInvoice is not null)
        {
            _ledger.RecordDuplicateAttempt(duplicateByInvoice.Hash, hash);
            return;
        }

        if (!_ledger.Claim(hash, receipt.InvoiceNumber, receipt.BusinessDateKey, sourceFile,
                receipt.Total, receipt.Currency, receipt.TransactionType))
        {
            // Claim performs the same checks under the ledger lock. This path is
            // the race-safe backstop if another job claimed it after our checks.
            var blocking = _ledger.FindBlockingByHash(hash)
                           ?? _ledger.FindBlockingByInvoice(
                               receipt.InvoiceNumber, receipt.BusinessDateKey, receipt.TransactionType);
            if (blocking is not null)
            {
                _ledger.RecordDuplicateAttempt(blocking.Hash, hash);
            }
            else
            {
                _log.Warn($"Could not claim job [{Short(hash)}]; another record already owns it. Not fiscalising.");
            }

            return;
        }

        var request = FiscalizeRequestFactory.Create(receipt, _config.Parse);

        // Serialised once and kept: a reconciliation has to replay the body the gateway already
        // saw, not a second interpretation of the same receipt text.
        var requestJson = JsonSerializer.Serialize(request, FiscalStackClient.RequestJsonOptions);
        _jobStore.SaveRequest(sourceFile, requestJson);

        _log.Info(
            $"Fiscalising {receipt.TransactionType} {receipt.InvoiceNumber ?? "(auto invoice)"} " +
            $"{receipt.Total:0.00} {receipt.Currency}, {receipt.Items.Count} item(s).");

        if (request.Currency is null && !string.IsNullOrWhiteSpace(receipt.Currency))
        {
            // The gateway records the sale in its own default currency, so the
            // fiscal receipt will not name the currency printed on the till roll.
            _log.Warn(
                $"{receipt.Currency} is not accepted by the fiscalisation route; " +
                "the fiscal receipt will carry the gateway's default currency.");
        }

        if (_offline.Enabled)
        {
            // Pending offline receipts own the next ZIMRA counters. Sync them
            // before any online sale so a live receipt cannot jump the chain.
            if (_offlineSync.HasPending)
            {
                var sync = await _offlineSync.SyncNowAsync(cancellationToken);
                if (_offlineSync.HasPending)
                {
                    await _offlineSync.RunExclusiveAsync(
                        async () =>
                        {
                            await IssueOfflineReceiptAsync(
                            hash, receipt, request, sync.Error, printFormat, originalJobPath, cancellationToken);
                            return true;
                        },
                        cancellationToken);
                    return;
                }
            }

            if (!await _client.IsReachableAsync(cancellationToken))
            {
                _log.Warn("FiscalStack API is unreachable; issuing a signed offline receipt.");
                await _offlineSync.RunExclusiveAsync(
                    async () =>
                    {
                        await IssueOfflineReceiptAsync(
                        hash, receipt, request, null, printFormat, originalJobPath, cancellationToken);
                        return true;
                    },
                    cancellationToken);
                return;
            }
        }

        var outcome = await _client.FiscaliseRawAsync(requestJson, cancellationToken);

        if (outcome.Status == FiscalizeOutcomeStatus.Success && outcome.Response is not null)
        {
            _offline.RecordOnlineSuccess(outcome.Response);
            _ledger.MarkFiscalised(hash, outcome.Response);
            _log.Info(
                $"Fiscalised [{Short(hash)}]: fiscalCode={outcome.Response.FiscalCode ?? "-"} " +
                $"receiptNo={outcome.Response.ReceiptNumber?.ToString() ?? "-"} " +
                $"day={outcome.Response.Receipt?.FiscalDayNo?.ToString() ?? "-"} " +
                $"counter={outcome.Response.Receipt?.ReceiptCounter?.ToString() ?? "-"}.");

            await PrintFiscalReceiptAsync(
                hash, receipt, outcome.Response, printFormat, originalJobPath, cancellationToken);
            return;
        }

        var status = outcome.Status == FiscalizeOutcomeStatus.Rejected
            ? LedgerStatus.Rejected
            : LedgerStatus.AmbiguousFailure;

        _ledger.MarkFailure(hash, status, outcome.Error ?? "Unknown fiscalisation error");

        if (status == LedgerStatus.Rejected)
        {
            _log.Error($"Fiscalisation rejected [{Short(hash)}]: {outcome.Error}. No receipt was issued or printed.");
        }
        else
        {
            _log.Error(
                $"Fiscalisation outcome is UNKNOWN [{Short(hash)}]: {outcome.Error}. " +
                "The receipt may exist in ZIMRA, so the job is not retried automatically. " +
                "Reconcile the invoice with the server before resubmitting.");
        }

        // No receipt is printed for rejected/ambiguous outcomes: the customer
        // must never receive a slip that looks fiscal but is not.
    }

    /// <summary>How many jobs with an unknown outcome a single pass looks at.</summary>
    private const int ReconcileBatchSize = 20;

    /// <summary>
    /// The gateway rounds its own tax buckets, so a cent or two between the till's total and the
    /// server's is arithmetic, not a different sale. Further apart means the number belongs to
    /// another document and adopting it would be a forgery.
    /// </summary>
    private const decimal ReconcileTotalTolerance = 0.05m;

    /// <summary>
    /// A job claimed less than this ago is still being fiscalised by the capture thread, so its
    /// outcome is not unknown yet. Longer than a request, its retries and its printing can take.
    /// </summary>
    private static readonly TimeSpan StaleInFlightAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the jobs whose fiscalisation outcome is unknown. A timeout or a 5xx means the
    /// receipt may already exist in ZIMRA, where printing nothing is a missing sale and printing
    /// again is a duplicate — so each one is looked up in the tenant's own invoice list instead.
    /// Found: the server's proof is adopted and the fiscal copy is printed. Absent: the body that
    /// timed out is replayed, which the gateway's unique-number check makes safe. Anything the
    /// pass cannot settle is left exactly as blocked, for the next pass or for the operator.
    /// </summary>
    /// <param name="force">
    /// The tray's manual pass: it runs even when reconcile.enabled is off and ignores the attempt
    /// budget, because an operator asking for it is the reason the budget exists.
    /// </param>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (!await _reconcileGate.WaitAsync(0, cancellationToken))
        {
            return new ReconcileReport(0, 0, 0, 0, 0, "Another reconciliation pass is already running.");
        }

        try
        {
            return await RunReconciliationAsync(cancellationToken, force);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task<ReconcileReport> RunReconciliationAsync(CancellationToken cancellationToken, bool force)
    {
        var reconcile = _config.Reconcile;

        if (!force && !reconcile.Enabled)
        {
            return new ReconcileReport(0, 0, 0, 0, 0, null);
        }

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            return new ReconcileReport(0, 0, 0, 0, 0, "apiKey is not configured, so nothing can be reconciled.");
        }

        var maxAttempts = force ? int.MaxValue : reconcile.MaxAttempts;
        var pending = _ledger.PendingReconcile(
            ReconcileBatchSize, maxAttempts, DateTimeOffset.Now, StaleInFlightAfter);

        if (pending.Count == 0)
        {
            return new ReconcileReport(0, 0, 0, 0, 0, null);
        }

        _log.Info($"Reconciling {pending.Count} job(s) whose fiscalisation outcome is unknown.");

        var adopted = 0;
        var resent = 0;
        var setAside = 0;
        var unknown = 0;

        foreach (var entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ledger.RecordReconcileAttempt(entry.Hash);

            if (string.IsNullOrWhiteSpace(entry.InvoiceNumber))
            {
                // Nothing to look for, and resending an auto-numbered receipt would create a
                // second one with a different number the gateway cannot recognise as a duplicate.
                unknown++;
                _log.Error(
                    $"Cannot reconcile [{Short(entry.Hash)}]: the receipt was sent without an invoice number, " +
                    "so the server cannot be asked which receipt it became and replaying it could create a second " +
                    "one. Check the FiscalStack dashboard, then reprint from the till if the sale is missing.");
                continue;
            }

            var first = await TryAdoptAsync(entry, cancellationToken);

            if (first == Adoption.Adopted)
            {
                adopted++;
                continue;
            }

            if (first == Adoption.Mismatched)
            {
                setAside++;
                continue;
            }

            if (first == Adoption.Failed)
            {
                // A list that could not be read says nothing about whether the receipt exists.
                // Resending on that answer is how a duplicate is created.
                unknown++;
                continue;
            }

            if (!reconcile.AutoResend)
            {
                unknown++;
                _log.Warn(
                    $"Invoice {entry.InvoiceNumber} is not on the server and reconcile.autoResend is off, " +
                    $"so [{Short(entry.Hash)}] was not re-sent.");
                continue;
            }

            var body = _jobStore.LoadRequest(entry.SourceFile);
            if (body is null)
            {
                setAside++;
                var reason =
                    "The receipt is not on the server and the request body was not retained, so it cannot be " +
                    "re-sent. Turn storage.retainJobs on, or fiscalise the sale again from the till.";
                _ledger.MarkFailure(entry.Hash, LedgerStatus.NeedsReview, reason);
                _log.Error($"Reconciliation stopped [{Short(entry.Hash)}]: {reason}");
                continue;
            }

            var outcome = await _client.FiscaliseRawAsync(body, cancellationToken);

            if (outcome.Status == FiscalizeOutcomeStatus.Success && outcome.Response is not null)
            {
                _offline.RecordOnlineSuccess(outcome.Response);
                _ledger.MarkFiscalised(entry.Hash, outcome.Response);
                resent++;
                _log.Info(
                    $"Re-sent [{Short(entry.Hash)}] and ZIMRA answered: fiscalCode=" +
                    $"{outcome.Response.FiscalCode ?? "-"} receiptNo=" +
                    $"{outcome.Response.ReceiptNumber?.ToString() ?? "-"}.");
                await PrintAdoptedAsync(entry, outcome.Response, cancellationToken);
                continue;
            }

            if (outcome.HttpStatus == 409)
            {
                // The number exists, so the timed-out request did land — after the list was read.
                // Asking again settles it; reporting a rejection would leave a fiscalised sale
                // that the till never printed.
                var second = await TryAdoptAsync(entry, cancellationToken);
                switch (second)
                {
                    case Adoption.Adopted:
                        adopted++;
                        continue;
                    case Adoption.Mismatched:
                        setAside++;
                        continue;
                    default:
                        unknown++;
                        _log.Error(
                            $"Re-sending [{Short(entry.Hash)}] was refused as a duplicate of " +
                            $"{entry.InvoiceNumber}, but the receipt could not be read back. It stays blocked.");
                        continue;
                }
            }

            if (outcome.Status == FiscalizeOutcomeStatus.Rejected)
            {
                setAside++;
                _ledger.MarkFailure(entry.Hash, LedgerStatus.Rejected, outcome.Error ?? "The gateway refused the replay.");
                _log.Error(
                    $"Re-sending [{Short(entry.Hash)}] was refused: {outcome.Error}. No receipt was printed.");
                continue;
            }

            unknown++;
            _ledger.MarkFailure(
                entry.Hash, LedgerStatus.AmbiguousFailure, outcome.Error ?? "The outcome is still unknown.");
            _log.Warn($"Re-sending [{Short(entry.Hash)}] did not get an answer either: {outcome.Error}.");
        }

        if (unknown > 0 && !force)
        {
            var remaining = _ledger
                .PendingReconcile(ReconcileBatchSize, reconcile.MaxAttempts, DateTimeOffset.Now, StaleInFlightAfter)
                .Count;
            var exhausted = _ledger.Count(LedgerStatus.AmbiguousFailure) - remaining;
            if (exhausted > 0)
            {
                _log.Warn(
                    $"{exhausted} job(s) have used all {reconcile.MaxAttempts} reconciliation passes. They stay " +
                    "blocked so the invoice number cannot be fiscalised twice; resolve them from the tray.");
            }
        }

        _log.Info(
            $"Reconciliation pass: {adopted} adopted, {resent} re-sent, {setAside} set aside, " +
            $"{unknown} still unknown.");

        return new ReconcileReport(pending.Count, adopted, resent, setAside, unknown, null);
    }

    private enum Adoption
    {
        NotFound,
        Adopted,
        Failed,
        Mismatched
    }

    /// <summary>
    /// Asks the server whether it already holds this invoice and, if it does, records its proof.
    /// </summary>
    private async Task<Adoption> TryAdoptAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        var lookup = await _client.FindInvoiceAsync(
            entry.InvoiceNumber!, _config.Reconcile.ScanPages, cancellationToken);

        if (lookup.Status == InvoiceLookupStatus.Failed)
        {
            _log.Warn(
                $"Could not read the invoice list for [{Short(entry.Hash)}]: {lookup.Error}. " +
                "The job stays blocked rather than being re-sent on an answer that means nothing.");
            return Adoption.Failed;
        }

        if (lookup.Status == InvoiceLookupStatus.Absent)
        {
            if (lookup.WindowExhausted)
            {
                _log.Warn(
                    $"The invoice walk for [{Short(entry.Hash)}] stopped after {lookup.PagesRead} page(s) " +
                    $"(reconcile.scanPages). A receipt older than that window is not found here.");
            }

            return Adoption.NotFound;
        }

        var invoice = lookup.Invoice!;

        if (invoice.Total is { } serverTotal &&
            Math.Abs(Math.Abs(serverTotal) - Math.Abs(entry.Total)) > ReconcileTotalTolerance)
        {
            var reason =
                $"The server has an invoice {invoice.InvoiceNumber} but its total is " +
                $"{serverTotal:0.00} {invoice.Currency ?? entry.Currency}, not {entry.Total:0.00} {entry.Currency}. " +
                "That is somebody else's receipt, so it was not adopted. Check the dashboard before resending.";
            _ledger.MarkFailure(entry.Hash, LedgerStatus.NeedsReview, reason);
            _log.Error($"Reconciliation stopped [{Short(entry.Hash)}]: {reason}");
            return Adoption.Mismatched;
        }

        _ledger.MarkFiscalisedFromServer(entry.Hash, invoice);
        _log.Info(
            $"Adopted the server's proof for [{Short(entry.Hash)}]: invoice={invoice.InvoiceNumber} " +
            $"fiscalCode={invoice.FiscalCode ?? "-"} receiptNo={invoice.ReceiptGlobalNo?.ToString() ?? "-"} " +
            $"day={invoice.FiscalDayNo?.ToString() ?? "-"} counter=" +
            $"{invoice.ReceiptCounter?.ToString() ?? "-"} (the list read carries no verification code, " +
            "so the copy prints without one).");

        await PrintAdoptedAsync(entry, invoice.ToResponse(), cancellationToken);
        return Adoption.Adopted;
    }

    /// <summary>
    /// Prints the fiscal copy for a job reconciliation settled. The receipt text comes back from
    /// the archive; without it the proof is still in the ledger, but there is nothing to rebuild
    /// the customer's copy from.
    /// </summary>
    private async Task PrintAdoptedAsync(LedgerEntry entry, FiscalizeResponse response, CancellationToken cancellationToken)
    {
        var sourceFile = entry.SourceFile;
        var text = _jobStore.LoadText(sourceFile);

        if (text is null)
        {
            _log.Warn(
                $"The receipt text for [{Short(entry.Hash)}] was not retained, so its fiscal copy cannot be rebuilt " +
                "here. The proof is in the ledger and prints from the till on the next reprint.");
            return;
        }

        var parse = _parser.Parse(text);
        if (parse.Status != ParseStatus.Parsed || parse.Receipt is null)
        {
            _log.Warn(
                $"The retained text for [{Short(entry.Hash)}] no longer parses " +
                $"({parse.Reason ?? "no reason given"}), so its fiscal copy was not printed.");
            return;
        }

        await PrintFiscalReceiptAsync(
            entry.Hash,
            parse.Receipt,
            response,
            ResolvePrintFormat(_jobStore.RetainedFormat(sourceFile)),
            _jobStore.RetainedOriginalPath(sourceFile),
            cancellationToken);
    }

    private async Task IssueOfflineReceiptAsync(
        string hash,
        ParsedReceipt receipt,
        FiscalizeRequest request,
        string? syncError,
        PrintFormat format,
        string? originalJobPath,
        CancellationToken cancellationToken)
    {
        if (!_offline.TryIssue(receipt, out var signed, out var error))
        {
            var reason = $"The API is offline and offline signing is not available: {error}";
            if (!string.IsNullOrWhiteSpace(syncError))
            {
                reason += $" (Previous sync stopped: {syncError})";
            }

            _log.Error($"Fiscalisation SKIPPED [{Short(hash)}]: {reason}. No receipt was printed.");
            _ledger.MarkFailure(hash, LedgerStatus.NeedsReview, reason);
            return;
        }

        var item = new OfflineQueueItem
        {
            JobHash = hash,
            CreatedAt = DateTimeOffset.Now,
            Request = _offline.PrepareSyncRequest(request, signed),
            Signature = signed.Signature,
            SignedHash = signed.Hash,
            VerificationCode = signed.VerificationCode,
            QrCodeUrl = signed.QrCodeUrl,
            ReceiptDate = signed.ReceiptDate,
            ReceiptCounter = signed.ReceiptCounter,
            ReceiptGlobalNo = signed.ReceiptGlobalNo,
            FiscalDayNo = signed.FiscalDayNo,
            ReceiptTotal = signed.ReceiptTotal
        };

        if (!_offlineQueue.TryEnqueue(item, out var queueError))
        {
            const string prefix = "The offline receipt was signed but could not be queued, so it was not printed.";
            _log.Error($"Fiscalisation SKIPPED [{Short(hash)}]: {prefix} {queueError}");
            _ledger.MarkFailure(hash, LedgerStatus.NeedsReview, $"{prefix} {queueError}");
            return;
        }

        _ledger.MarkFiscalisedOffline(hash, signed);
        _log.Warn(
            $"Issued OFFLINE fiscal receipt [{Short(hash)}]: global={signed.ReceiptGlobalNo} " +
            $"counter={signed.ReceiptCounter} total={signed.ReceiptTotal:0.00} {signed.Currency}. " +
            $"It is queued for sync ({_offlineQueue.Count} pending).");

        await PrintModelAsync(
            hash, BuildOfflinePrintModel(receipt, signed, format, originalJobPath), cancellationToken);
    }

    private ReceiptPrintModel BuildOfflinePrintModel(
        ParsedReceipt receipt,
        OfflineSignedReceipt signed,
        PrintFormat format,
        string? originalJobPath)
    {
        var priceSign = string.Equals(receipt.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;

        var dateText = DateTimeOffset.TryParse(
            signed.ReceiptDate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed.LocalDateTime.ToString("dd/MM/yyyy HH:mm")
            : DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm");

        return new ReceiptPrintModel
        {
            Format = format,
            CompanyName = _company.Name,
            CompanyTin = _company.Tin,
            CompanyVatNumber = _company.VatNumber,
            CompanyAddress = _company.Address,
            CompanyPhone = _company.Phone,
            CompanyEmail = _company.Email,
            CompanyLogoPath = _company.ResolveLogoPath(ResolveConfiguredLogoPath()),
            InvoiceNumber = receipt.InvoiceNumber ?? $"OFF-{signed.ReceiptGlobalNo}",
            DateText = dateText,
            Currency = signed.Currency,
            PaymentMethod = receipt.PaymentMethod,
            TransactionType = receipt.TransactionType,
            RelatedInvoiceNumber = receipt.RelatedInvoiceNumber,
            BuyerName = receipt.Buyer.Name,
            BuyerAddress = receipt.Buyer.Address,
            BuyerTin = receipt.Buyer.Tin,
            BuyerVatNumber = receipt.Buyer.VatNumber,
            BuyerEmail = receipt.Buyer.Email,
            BuyerPhone = receipt.Buyer.Phone,
            Items = receipt.Items
                .Select(item => new ReceiptPrintLine(
                    item.Name,
                    item.Quantity,
                    item.Amount * priceSign,
                    item.UnitPrice * priceSign,
                    item.TaxRate * 100m,
                    item.TaxType))
                .ToList(),
            Taxes = signed.Taxes
                .Select(tax => new ReceiptPrintTax(
                    tax.TaxId == 1 ? null : tax.TaxPercent,
                    tax.TaxId == 1,
                    tax.SalesAmountWithTax - tax.TaxAmount,
                    tax.TaxAmount,
                    tax.SalesAmountWithTax))
                .ToList(),
            Total = signed.ReceiptTotal,
            TaxTotal = signed.TaxTotal,
            Subtotal = signed.ReceiptTotal - signed.TaxTotal,
            TaxInclusive = IsTaxInclusive,
            FiscalCode = signed.Hash,
            VerificationCode = signed.VerificationCode,
            QrCodeUrl = signed.QrCodeUrl,
            VerifyUrl = VerifyUrl(signed.QrCodeUrl),
            DeviceId = CurrentDeviceId(),
            DeviceSerialNo = _offline.DeviceSerialNo,
            ReceiptNumber = signed.ReceiptGlobalNo,
            ReceiptCounter = signed.ReceiptCounter,
            ReceiptGlobalNo = signed.ReceiptGlobalNo,
            FiscalDayNo = signed.FiscalDayNo,
            Notes = receipt.CreditNoteReason,
            IsOffline = true,
            Template = ResolveTemplate(originalJobPath),
            OriginalJobPath = originalJobPath,
            PrinterName = _config.Printing.PrinterName,
            A4PrinterName = _config.Printing.A4PrinterName,
            PaperWidthMm = _config.Printing.PaperWidthMm,
            Copies = _config.Printing.Copies,
            AccentColor = _config.Printing.AccentColor,
            FooterText = _config.Printing.FooterText
        };
    }

    private Task PrintFiscalReceiptAsync(
        string hash,
        ParsedReceipt receipt,
        FiscalizeResponse response,
        PrintFormat format,
        string? originalJobPath,
        CancellationToken cancellationToken)
    {
        return PrintModelAsync(
            hash, BuildPrintModel(receipt, response, format, originalJobPath), cancellationToken);
    }

    /// <summary>
    /// Reprints the fiscal copy stored for a job. Only entries with a snapshot
    /// can be reprinted; the fiscalisation result is never repeated.
    /// </summary>
    public bool TryReprint(string hash, out string error)
    {
        var model = _snapshots.Load(hash);
        if (model is null)
        {
            error = "No print snapshot was stored for this job, so it cannot be reprinted here.";
            return false;
        }

        model.IsReprint = true;
        var printerName = string.IsNullOrWhiteSpace(model.EffectivePrinterName)
            ? "the default printer"
            : model.EffectivePrinterName;

        try
        {
            PrintWithLock(model);
            _ledger.MarkReprinted(hash, printerName);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            _ledger.SetPrintResult(hash, "ReprintFailed", ex.Message);
            _log.Error($"Reprinting {Short(hash)} failed", ex);
            error = ex.Message;
            return false;
        }
    }

    private void PrintWithLock(ReceiptPrintModel model)
    {
        lock (_printGate)
        {
            _printer.Print(model);
        }
    }

    private async Task PrintModelAsync(
        string hash,
        ReceiptPrintModel model,
        CancellationToken cancellationToken)
    {
        _snapshots.Save(hash, model);

        if (!_config.Printing.Enabled)
        {
            _ledger.SetPrintResult(hash, "NotRequested", null);
            _log.Info("Receipt printing is disabled; skipped the fiscal copy.");
            return;
        }

        _ledger.SetPrintResult(hash, "Printing", null);

        string? lastError = null;
        for (var attempt = 0; attempt <= _config.Printing.RetryCount; attempt++)
        {
            try
            {
                PrintWithLock(model);
                _ledger.SetPrintResult(hash, "Printed", null);
                _log.Info($"{(model.IsOffline ? "Offline" : "Fiscal")} receipt printed on '{model.EffectivePrinterName}'.");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _log.Error(
                    $"Printing the fiscal receipt failed (attempt {attempt + 1}/{_config.Printing.RetryCount + 1})",
                    ex);

                if (attempt < _config.Printing.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }

        _ledger.SetPrintResult(hash, "Failed", lastError);
        _log.Error(
            "The receipt WAS fiscalised but not printed. It will not be fiscalised again; " +
            $"the fiscal proof is in the ledger under {Short(hash)}.");
    }

    private ReceiptPrintModel BuildPrintModel(
        ParsedReceipt receipt,
        FiscalizeResponse response,
        PrintFormat format,
        string? originalJobPath)
    {
        var fiscalReceipt = response.Receipt;

        IReadOnlyList<ReceiptPrintLine> lines;
        if (fiscalReceipt?.ReceiptLines is { Count: > 0 } responseLines)
        {
            lines = responseLines
                .Select((line, index) =>
                {
                    var source = index < receipt.Items.Count ? receipt.Items[index] : null;
                    return new ReceiptPrintLine(
                        line.ReceiptLineName ?? source?.Name ?? "Item",
                        line.ReceiptLineQuantity ?? source?.Quantity ?? 0,
                        line.ReceiptLineTotal ?? source?.Amount ?? 0,
                        line.ReceiptLinePrice ?? source?.UnitPrice,
                        source is null ? null : source.TaxRate * 100m,
                        source?.TaxType);
                })
                .ToList();
        }
        else
        {
            lines = receipt.Items
                .Select(item => new ReceiptPrintLine(
                    item.Name,
                    item.Quantity,
                    item.Amount,
                    item.UnitPrice,
                    item.TaxRate * 100m,
                    item.TaxType))
                .ToList();
        }

        var taxTotal = fiscalReceipt?.ReceiptTaxes is { Count: > 0 } taxes
            ? taxes.Sum(tax => tax.TaxAmount ?? 0m)
            : receipt.TaxTotal;
        var total = fiscalReceipt?.ReceiptTotal ?? receipt.Total;
        var invoiceNumber = fiscalReceipt?.InvoiceNo ?? receipt.InvoiceNumber ?? response.ReceiptNumber?.ToString() ?? "-";
        var deviceId = CurrentDeviceId();

        return new ReceiptPrintModel
        {
            Format = format,
            CompanyName = _company.Name,
            CompanyTin = _company.Tin,
            CompanyVatNumber = _company.VatNumber,
            CompanyAddress = _company.Address,
            CompanyPhone = _company.Phone,
            CompanyEmail = _company.Email,
            CompanyLogoPath = _company.ResolveLogoPath(ResolveConfiguredLogoPath()),
            InvoiceNumber = invoiceNumber,
            DateText = (fiscalReceipt?.ReceiptDate ?? receipt.Date ?? DateTimeOffset.Now)
                .LocalDateTime.ToString("dd/MM/yyyy HH:mm"),
            Currency = fiscalReceipt?.ReceiptCurrency ?? receipt.Currency,
            PaymentMethod = receipt.PaymentMethod,
            TransactionType = receipt.TransactionType,
            RelatedInvoiceNumber = receipt.RelatedInvoiceNumber,
            BuyerName = fiscalReceipt?.BuyerData?.BuyerRegisterName ?? receipt.Buyer.Name,
            BuyerAddress = receipt.Buyer.Address,
            BuyerTin = fiscalReceipt?.BuyerData?.BuyerTin ?? receipt.Buyer.Tin,
            BuyerVatNumber = fiscalReceipt?.BuyerData?.VatNumber ?? receipt.Buyer.VatNumber,
            BuyerEmail = receipt.Buyer.Email,
            BuyerPhone = receipt.Buyer.Phone,
            Items = lines,
            Taxes = BuildTaxRows(fiscalReceipt, receipt),
            Total = total,
            TaxTotal = taxTotal,
            Subtotal = total - taxTotal,
            TaxInclusive = IsTaxInclusive,
            FiscalCode = response.FiscalCode,
            VerificationCode = response.VerificationCode,
            QrCodeUrl = response.QrCode,
            VerifyUrl = VerifyUrl(response.QrCode),
            DeviceId = deviceId,
            DeviceSerialNo = _offline.DeviceSerialNo,
            ReceiptNumber = response.ReceiptNumber,
            ReceiptCounter = fiscalReceipt?.ReceiptCounter,
            ReceiptGlobalNo = fiscalReceipt?.ReceiptGlobalNo,
            FiscalDayNo = fiscalReceipt?.FiscalDayNo,
            Notes = receipt.CreditNoteReason,
            Template = ResolveTemplate(originalJobPath),
            OriginalJobPath = originalJobPath,
            PrinterName = _config.Printing.PrinterName,
            A4PrinterName = _config.Printing.A4PrinterName,
            PaperWidthMm = _config.Printing.PaperWidthMm,
            Copies = _config.Printing.Copies,
            AccentColor = _config.Printing.AccentColor,
            FooterText = _config.Printing.FooterText
        };
    }

    private bool IsTaxInclusive => string.Equals(_config.Parse.VatFlag.Trim(), "1", StringComparison.Ordinal);

    private PrintFormat ResolvePrintFormat(JobFormat jobFormat) =>
        _config.Printing.Format.Trim().ToLowerInvariant() switch
        {
            "a4" => PrintFormat.A4,
            "auto" => jobFormat is JobFormat.Pdf or JobFormat.PostScript ? PrintFormat.A4 : PrintFormat.Thermal,
            _ => PrintFormat.Thermal
        };

    /// <summary>
    /// The original template needs the retained POS bytes; without them (retention
    /// off, or the write failed) the fiscal template is printed so the customer
    /// still leaves with a fiscal document.
    /// </summary>
    private PrintTemplate ResolveTemplate(string? originalJobPath)
    {
        if (_config.Printing.Template != "original")
        {
            return PrintTemplate.Fiscal;
        }

        if (string.IsNullOrWhiteSpace(originalJobPath) || !File.Exists(originalJobPath))
        {
            _log.Warn("printing.template is 'original' but the job's source bytes were not retained; using the fiscal template.");
            return PrintTemplate.Fiscal;
        }

        return PrintTemplate.Original;
    }

    private string CurrentDeviceId()
    {
        var deviceId = _offline.EffectiveDeviceId;
        return deviceId == "0" ? "" : deviceId;
    }

    private string? ResolveConfiguredLogoPath()
    {
        var logo = _config.Company.LogoPath;
        if (string.IsNullOrWhiteSpace(logo))
        {
            return null;
        }

        if (Path.IsPathRooted(logo))
        {
            return logo;
        }

        var baseDirectory = string.IsNullOrWhiteSpace(_config.ConfigPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(_config.ConfigPath)) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDirectory, logo));
    }

    private static string VerifyUrl(string? qrCodeUrl) =>
        !string.IsNullOrWhiteSpace(qrCodeUrl) &&
        Uri.TryCreate(qrCodeUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "https://fdms.zimra.co.zw";

    private static List<ReceiptPrintTax> BuildTaxRows(FiscalReceipt? fiscalReceipt, ParsedReceipt receipt)
    {
        if (fiscalReceipt?.ReceiptTaxes is { Count: > 0 } serverTaxes)
        {
            return serverTaxes
                .Select(tax =>
                {
                    var gross = tax.SalesAmountWithTax ?? 0m;
                    var amount = tax.TaxAmount ?? 0m;
                    var percent = tax.TaxPercent;
                    var exempt = percent is null or 0m && receipt.Items.Any(item =>
                        item.TaxType.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase) &&
                        (percent is null || Math.Abs(item.TaxRate * 100m - percent.Value) < 0.01m));

                    return new ReceiptPrintTax(
                        exempt || percent is null ? null : percent,
                        exempt,
                        gross - amount,
                        amount,
                        gross);
                })
                .ToList();
        }

        return receipt.Items
            .GroupBy(item => new
            {
                Rate = Math.Round(item.TaxRate * 100m, 2),
                Exempt = item.TaxType.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase)
            })
            .Select(group =>
            {
                var gross = group.Sum(item => item.Amount);
                var net = group.Key.Exempt || group.Key.Rate == 0m
                    ? gross
                    : gross / (1m + group.Key.Rate / 100m);
                return new ReceiptPrintTax(
                    group.Key.Exempt ? null : group.Key.Rate,
                    group.Key.Exempt,
                    net,
                    gross - net,
                    gross);
            })
            .ToList();
    }

    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];
}

/// <summary>
/// What one reconciliation pass did. <see cref="StillUnknown"/> is the number left blocked for the
/// next pass, so a run that settled nothing is visible instead of silent.
/// </summary>
public sealed record ReconcileReport(
    int Scanned,
    int Adopted,
    int Resent,
    int SetAside,
    int StillUnknown,
    string? Error = null)
{
    public string Summary => Scanned == 0
        ? Error ?? "Nothing to reconcile."
        : $"{Adopted} adopted, {Resent} re-sent, {SetAside} set aside, {StillUnknown} still unknown" +
          (Error is null ? "" : $" ({Error})");
}
