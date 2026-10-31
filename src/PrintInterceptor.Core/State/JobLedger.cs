using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;

namespace PrintInterceptor.Core.State;

public enum LedgerStatus
{
    InFlight,
    Fiscalised,
    FiscalisedOffline,
    Rejected,
    NeedsReview,
    AmbiguousFailure
}

public sealed class LedgerEntry
{
    public string Hash { get; set; } = "";
    public string? InvoiceNumber { get; set; }
    public string BusinessDate { get; set; } = "";
    public LedgerStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "";
    public string? FiscalCode { get; set; }
    public string? QrCode { get; set; }
    public string? VerificationCode { get; set; }
    public long? ReceiptNumber { get; set; }
    public int? ReceiptCounter { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public int? FiscalDayNo { get; set; }
    public string PrintStatus { get; set; } = "NotRequested";
    public string? PrintError { get; set; }
    public string? Error { get; set; }
    public string? SourceFile { get; set; }
    public string? OfflineSignature { get; set; }
    public string? OfflineReceiptDate { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    public int ReprintCount { get; set; }

    /// <summary>Automatic reprints suppressed before they could reach FiscalStack.</summary>
    public int DuplicateAttemptCount { get; set; }
    public DateTimeOffset? LastDuplicateAttemptAt { get; set; }
    public string? LastDuplicateAttemptHash { get; set; }
}

internal sealed class LedgerDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, LedgerEntry> Entries { get; set; } = new();
}

/// <summary>
/// Counts and the newest entry for each alert bucket the tray watches.
/// Snapshots are compared between refreshes so the operator is only notified
/// when the number of failures actually increases.
/// </summary>
public sealed record FailureSnapshot(
    int Rejected,
    int Ambiguous,
    int PrintFailed,
    LedgerEntry? LatestRejected,
    LedgerEntry? LatestAmbiguous,
    LedgerEntry? LatestPrintFailure);

/// <summary>
/// Durable record of every fiscalisation attempt. A claim is written before the
/// API call, so a crash can never silently produce a second ZIMRA receipt.
/// </summary>
public sealed class JobLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILog _log;
    private LedgerDocument _document;

    public JobLedger(string stateDirectory, ILog log)
    {
        _log = log;
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "ledger.json");
        _document = Load();
    }

    public LedgerEntry? Get(string hash)
    {
        lock (_gate)
        {
            return _document.Entries.TryGetValue(hash, out var entry) ? Clone(entry) : null;
        }
    }

    /// <summary>
    /// Every entry, newest first, for the operator jobs window.
    /// </summary>
    public IReadOnlyList<LedgerEntry> All()
    {
        lock (_gate)
        {
            return _document.Entries.Values
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public int Count(LedgerStatus status)
    {
        lock (_gate)
        {
            return _document.Entries.Values.Count(entry => entry.Status == status);
        }
    }

    /// <summary>
    /// Counts the entries the operator should be warned about: rejected and
    /// ambiguous fiscalisations, plus receipts that made it to FiscalStack but
    /// could not be printed. Also returns the most recent entry per bucket so
    /// the tray can include the invoice number and error text in the alert.
    /// </summary>
    public FailureSnapshot GetFailureSnapshot()
    {
        lock (_gate)
        {
            LedgerEntry? latestRejected = null;
            LedgerEntry? latestAmbiguous = null;
            LedgerEntry? latestPrintFailure = null;
            var rejected = 0;
            var ambiguous = 0;
            var printFailed = 0;

            foreach (var entry in _document.Entries.Values)
            {
                switch (entry.Status)
                {
                    case LedgerStatus.Rejected:
                        rejected++;
                        if (IsMoreRecent(entry, latestRejected)) latestRejected = entry;
                        break;
                    case LedgerStatus.AmbiguousFailure:
                        ambiguous++;
                        if (IsMoreRecent(entry, latestAmbiguous)) latestAmbiguous = entry;
                        break;
                }

                if (IsPrintFailure(entry))
                {
                    printFailed++;
                    if (IsMoreRecent(entry, latestPrintFailure)) latestPrintFailure = entry;
                }
            }

            return new FailureSnapshot(
                rejected,
                ambiguous,
                printFailed,
                latestRejected is null ? null : Clone(latestRejected),
                latestAmbiguous is null ? null : Clone(latestAmbiguous),
                latestPrintFailure is null ? null : Clone(latestPrintFailure));
        }
    }

    private static bool IsPrintFailure(LedgerEntry entry) =>
        entry.PrintStatus is "Failed" or "ReprintFailed";

    private static bool IsMoreRecent(LedgerEntry candidate, LedgerEntry? current) =>
        current is null || candidate.UpdatedAt > current.UpdatedAt;

    public LedgerEntry? FindBlockingByHash(string hash)
    {
        lock (_gate)
        {
            return _document.Entries.TryGetValue(hash, out var entry) && IsBlocking(entry.Status)
                ? Clone(entry)
                : null;
        }
    }

    public LedgerEntry? FindBlockingByInvoice(string? invoiceNumber, string businessDate)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return null;
        }

        lock (_gate)
        {
            return _document.Entries.Values
                .Where(entry => IsBlocking(entry.Status))
                .Where(entry => string.Equals(entry.InvoiceNumber, invoiceNumber, StringComparison.OrdinalIgnoreCase))
                .Where(entry => entry.BusinessDate == businessDate)
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Audits a reprint that was suppressed before fiscalisation. The original
    /// receipt stays untouched apart from these counters; no new claim is created
    /// for the duplicate job and therefore no second API request can be sent.
    /// </summary>
    public void RecordDuplicateAttempt(string originalHash, string duplicateHash)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(originalHash, out var entry))
            {
                _log.Error(
                    $"A duplicate job {Short(duplicateHash)} referred to missing ledger entry {Short(originalHash)}; " +
                    "it was not fiscalised.");
                return;
            }

            entry.DuplicateAttemptCount++;
            entry.LastDuplicateAttemptAt = DateTimeOffset.Now;
            entry.LastDuplicateAttemptHash = duplicateHash;
            entry.UpdatedAt = entry.LastDuplicateAttemptAt.Value;
            SaveLocked();

            _log.Warn(
                $"DUPLICATE PRINT SUPPRESSED: invoice={entry.InvoiceNumber ?? "-"} " +
                $"businessDate={entry.BusinessDate} duplicateJob={Short(duplicateHash)} " +
                $"originalJob={Short(entry.Hash)} originalStatus={entry.Status} " +
                $"fiscalCode={entry.FiscalCode ?? "-"} receiptNo={entry.ReceiptNumber?.ToString() ?? "-"} " +
                $"attempts={entry.DuplicateAttemptCount}. No second fiscal receipt was created.");
        }
    }

    /// <summary>
    /// Atomically reserves a job before fiscalisation. Returns false when a
    /// blocking record exists (already fiscalised, in flight, or ambiguous).
    /// </summary>
    public bool Claim(
        string hash,
        string? invoiceNumber,
        string businessDate,
        string? sourceFile,
        decimal total,
        string currency)
    {
        lock (_gate)
        {
            if (_document.Entries.TryGetValue(hash, out var existing) && IsBlocking(existing.Status))
            {
                return false;
            }

            if (FindBlockingByInvoiceLocked(invoiceNumber, businessDate) is not null)
            {
                return false;
            }

            var now = DateTimeOffset.Now;
            var entry = existing ?? new LedgerEntry { Hash = hash, CreatedAt = now };
            entry.InvoiceNumber = invoiceNumber;
            entry.BusinessDate = businessDate;
            entry.Status = LedgerStatus.InFlight;
            entry.Total = total;
            entry.Currency = currency;
            entry.SourceFile = sourceFile;
            entry.Error = null;
            entry.UpdatedAt = now;
            entry.PrintStatus = "NotRequested";
            entry.PrintError = null;

            _document.Entries[hash] = entry;
            SaveLocked();
            return true;
        }
    }

    public void MarkFiscalised(string hash, FiscalizeResponse response)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                _log.Error($"Ledger entry {hash[..Math.Min(12, hash.Length)]} disappeared before success was recorded.");
                return;
            }

            entry.Status = LedgerStatus.Fiscalised;
            entry.FiscalCode = response.FiscalCode;
            entry.QrCode = response.QrCode;
            entry.VerificationCode = response.VerificationCode;
            entry.ReceiptNumber = response.ReceiptNumber;
            entry.ReceiptCounter = response.Receipt?.ReceiptCounter;
            entry.ReceiptGlobalNo = response.Receipt?.ReceiptGlobalNo;
            entry.FiscalDayNo = response.Receipt?.FiscalDayNo;
            entry.InvoiceNumber = response.Receipt?.InvoiceNo ?? entry.InvoiceNumber;
            entry.Error = null;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    public void MarkFiscalisedOffline(string hash, OfflineSignedReceipt signed)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                entry = new LedgerEntry { Hash = hash, CreatedAt = DateTimeOffset.Now };
                _document.Entries[hash] = entry;
            }

            entry.Status = LedgerStatus.FiscalisedOffline;
            entry.FiscalCode = signed.Hash;
            entry.QrCode = signed.QrCodeUrl;
            entry.VerificationCode = signed.VerificationCode;
            entry.ReceiptNumber = signed.ReceiptGlobalNo;
            entry.ReceiptCounter = signed.ReceiptCounter;
            entry.ReceiptGlobalNo = signed.ReceiptGlobalNo;
            entry.FiscalDayNo = signed.FiscalDayNo;
            entry.Total = signed.ReceiptTotal;
            entry.Currency = signed.Currency;
            entry.OfflineSignature = signed.Signature;
            entry.OfflineReceiptDate = signed.ReceiptDate;
            entry.Error = null;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    /// <summary>
    /// Promotes an offline receipt to the server-confirmed record after sync.
    /// The server is authoritative for the final hash and counters.
    /// </summary>
    public void MarkSynced(string hash, FiscalizeResponse response)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                entry = new LedgerEntry { Hash = hash, CreatedAt = DateTimeOffset.Now };
                _document.Entries[hash] = entry;
            }

            entry.Status = LedgerStatus.Fiscalised;
            entry.FiscalCode = response.FiscalCode ?? entry.FiscalCode;
            entry.QrCode = response.QrCode ?? entry.QrCode;
            entry.VerificationCode = response.VerificationCode ?? entry.VerificationCode;
            entry.ReceiptNumber = response.ReceiptNumber ?? entry.ReceiptNumber;
            entry.ReceiptCounter = response.Receipt?.ReceiptCounter ?? entry.ReceiptCounter;
            entry.ReceiptGlobalNo = response.Receipt?.ReceiptGlobalNo ?? entry.ReceiptGlobalNo;
            entry.FiscalDayNo = response.Receipt?.FiscalDayNo ?? entry.FiscalDayNo;
            entry.InvoiceNumber = response.Receipt?.InvoiceNo ?? entry.InvoiceNumber;
            entry.SyncedAt = DateTimeOffset.Now;
            entry.Error = null;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    public void MarkFailure(string hash, LedgerStatus status, string error)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                entry = new LedgerEntry { Hash = hash, CreatedAt = DateTimeOffset.Now };
                _document.Entries[hash] = entry;
            }

            entry.Status = status;
            entry.Error = error;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    public void RecordNeedsReview(
        string hash,
        string? invoiceNumber,
        string businessDate,
        string reason,
        string? sourceFile)
    {
        lock (_gate)
        {
            if (_document.Entries.TryGetValue(hash, out var existing) && IsBlocking(existing.Status))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var entry = existing ?? new LedgerEntry { Hash = hash, CreatedAt = now };
            entry.InvoiceNumber = invoiceNumber;
            entry.BusinessDate = businessDate;
            entry.Status = LedgerStatus.NeedsReview;
            entry.Error = reason;
            entry.SourceFile = sourceFile;
            entry.UpdatedAt = now;
            _document.Entries[hash] = entry;
            SaveLocked();
        }
    }

    /// <summary>
    /// Records an operator reprint. The fiscalisation result is unchanged;
    /// only the copy counter and print status move.
    /// </summary>
    public void MarkReprinted(string hash, string printerName)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                return;
            }

            entry.ReprintCount++;
            entry.PrintStatus = "Reprinted";
            entry.PrintError = null;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
            _log.Info(
                $"Ledger entry {hash[..Math.Min(12, hash.Length)]} reprinted on '{printerName}' " +
                $"(copy {entry.ReprintCount}).");
        }
    }

    public void SetPrintResult(string hash, string printStatus, string? error)
    {
        lock (_gate)
        {
            if (!_document.Entries.TryGetValue(hash, out var entry))
            {
                return;
            }

            entry.PrintStatus = printStatus;
            entry.PrintError = error;
            entry.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    private LedgerEntry? FindBlockingByInvoiceLocked(string? invoiceNumber, string businessDate)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return null;
        }

        return _document.Entries.Values
            .Where(entry => IsBlocking(entry.Status))
            .FirstOrDefault(entry =>
                string.Equals(entry.InvoiceNumber, invoiceNumber, StringComparison.OrdinalIgnoreCase) &&
                entry.BusinessDate == businessDate);
    }

    private static bool IsBlocking(LedgerStatus status) =>
        status is LedgerStatus.InFlight or LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline
            or LedgerStatus.AmbiguousFailure;

    private LedgerDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new LedgerDocument();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var document = JsonSerializer.Deserialize<LedgerDocument>(json, JsonOptions);
            if (document is null)
            {
                throw new InvalidOperationException("ledger.json is empty");
            }

            _log.Info($"Ledger loaded: {document.Entries.Count} entries.");
            return document;
        }
        catch (Exception ex)
        {
            // Never start with an empty ledger after a successful fiscalisation:
            // that would allow duplicate ZIMRA receipts.
            throw new InvalidOperationException(
                $"Could not read the fiscalisation ledger at {_path}. " +
                "Fix or restore it before starting the service.", ex);
        }
    }

    private void SaveLocked()
    {
        var json = JsonSerializer.Serialize(_document, JsonOptions);
        var temp = _path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];

    private static LedgerEntry Clone(LedgerEntry entry) => new()
    {
        Hash = entry.Hash,
        InvoiceNumber = entry.InvoiceNumber,
        BusinessDate = entry.BusinessDate,
        Status = entry.Status,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        Total = entry.Total,
        Currency = entry.Currency,
        FiscalCode = entry.FiscalCode,
        QrCode = entry.QrCode,
        VerificationCode = entry.VerificationCode,
        ReceiptNumber = entry.ReceiptNumber,
        ReceiptCounter = entry.ReceiptCounter,
        ReceiptGlobalNo = entry.ReceiptGlobalNo,
        FiscalDayNo = entry.FiscalDayNo,
        PrintStatus = entry.PrintStatus,
        PrintError = entry.PrintError,
        Error = entry.Error,
        SourceFile = entry.SourceFile,
        OfflineSignature = entry.OfflineSignature,
        OfflineReceiptDate = entry.OfflineReceiptDate,
        SyncedAt = entry.SyncedAt,
        ReprintCount = entry.ReprintCount,
        DuplicateAttemptCount = entry.DuplicateAttemptCount,
        LastDuplicateAttemptAt = entry.LastDuplicateAttemptAt,
        LastDuplicateAttemptHash = entry.LastDuplicateAttemptHash
    };
}
