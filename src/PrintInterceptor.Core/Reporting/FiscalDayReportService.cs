using System.Globalization;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor.Core.Reporting;

/// <summary>
/// Builds the day report from what the till keeps: the ledger says which documents belong to the
/// date and what is still outstanding, and the printed fiscal copy behind each document supplies
/// the tax buckets and the lines. Receipts are never re-parsed for this, so the figures are the
/// ones that went to the gateway and came back on the paper.
/// </summary>
public sealed class FiscalDayReportService
{
    private readonly InterceptorConfig _config;
    private readonly JobLedger _ledger;
    private readonly PrintSnapshotStore _snapshots;
    private readonly ILog _log;

    public FiscalDayReportService(
        InterceptorConfig config,
        JobLedger ledger,
        PrintSnapshotStore snapshots,
        ILog log)
    {
        _config = config;
        _ledger = ledger;
        _snapshots = snapshots;
        _log = log;
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The report for one business date. Reads local state only, so it works with the network down
    /// and changes nothing: a day report is a statement of what already happened.
    /// </summary>
    public FiscalDayReport Build(DateOnly date)
    {
        var key = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var businessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sales = new KindAccumulator();
        var credits = new KindAccumulator();
        var debits = new KindAccumulator();
        var identity = new DocumentIdentity();
        var lines = new List<FiscalDayLine>();
        var withoutDetail = new List<string>();
        var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        long? firstReceipt = null, lastReceipt = null;
        int? firstCounter = null, lastCounter = null;
        int waitingForSync = 0, heldForReview = 0, rejected = 0, unknown = 0, inFlight = 0;

        foreach (var entry in _ledger.ForBusinessDate(key))
        {
            if (entry.Status is LedgerStatus.NeedsReview)
            {
                heldForReview++;
                continue;
            }

            if (entry.Status is LedgerStatus.Rejected)
            {
                rejected++;
                continue;
            }

            if (entry.Status is LedgerStatus.AmbiguousFailure)
            {
                unknown++;
                continue;
            }

            if (entry.Status is LedgerStatus.InFlight)
            {
                inFlight++;
                continue;
            }

            var kind = string.Equals(entry.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase)
                ? credits
                : string.Equals(entry.TransactionType, "DebitNote", StringComparison.OrdinalIgnoreCase)
                    ? debits
                    : sales;

            kind.Documents++;
            AddCurrency(currencies, entry.Currency);

            if (entry.Status == LedgerStatus.FiscalisedOffline && entry.SyncedAt is null)
            {
                waitingForSync++;
            }

            var model = _snapshots.LoadDurable(entry.Hash);
            if (model is null)
            {
                // Issued, but the printed copy is gone from this till, so its buckets cannot be
                // known here. Folding it into the totals would move VAT on an assumption.
                kind.GrossWithoutDetail += entry.Total;
                withoutDetail.Add(
                    $"{entry.InvoiceNumber ?? "(no number)"} {Money(entry.Total)} {entry.Currency}".TrimEnd());
                continue;
            }

            kind.Add(model);
            identity.Take(model);
            AddCurrency(currencies, model.Currency);

            var receiptNumber = entry.ReceiptNumber ?? model.ReceiptNumber;
            if (receiptNumber is { } number)
            {
                firstReceipt = Min(firstReceipt, number);
                lastReceipt = Max(lastReceipt, number);
            }

            var counter = model.ReceiptCounter ?? entry.ReceiptCounter;
            if (counter is { } counted)
            {
                firstCounter = Min(firstCounter, counted);
                lastCounter = Max(lastCounter, counted);
            }

            identity.FiscalDayNo ??= model.FiscalDayNo ?? entry.FiscalDayNo;

            foreach (var item in model.Items)
            {
                lines.Add(new FiscalDayLine(
                    businessDate,
                    model.InvoiceNumber,
                    entry.TransactionType ?? model.TransactionType,
                    item.Name,
                    item.Quantity,
                    item.UnitPrice,
                    item.Amount,
                    LineTaxType(item),
                    item.TaxRate,
                    entry.FiscalCode ?? model.FiscalCode,
                    receiptNumber,
                    counter,
                    model.ReceiptGlobalNo ?? entry.ReceiptGlobalNo,
                    model.FiscalDayNo ?? entry.FiscalDayNo,
                    TrimToNull(model.DeviceId),
                    TrimToNull(model.DeviceSerialNo),
                    model.Currency,
                    model.IsOffline,
                    model.BuyerName,
                    model.BuyerTin));
            }
        }

        var report = new FiscalDayReport
        {
            BusinessDate = businessDate,
            CompanyName = identity.Company ?? TrimToNull(_config.Company.Name),
            CompanyTin = identity.Tin ?? TrimToNull(_config.Company.Tin),
            CompanyVatNumber = identity.VatNumber ?? TrimToNull(_config.Company.VatNumber),
            DeviceId = identity.DeviceId,
            DeviceSerialNo = identity.DeviceSerialNo,
            FiscalDayNo = identity.FiscalDayNo,
            FirstReceiptNumber = firstReceipt,
            LastReceiptNumber = lastReceipt,
            FirstCounter = firstCounter,
            LastCounter = lastCounter,
            Sales = sales.ToKind(),
            Credits = credits.ToKind(),
            Debits = debits.ToKind(),
            WaitingForSync = waitingForSync,
            HeldForReview = heldForReview,
            Rejected = rejected,
            UnknownOutcome = unknown,
            InFlight = inFlight,
            WithoutItemDetail = withoutDetail,
            Lines = lines,
            Currencies = currencies.ToList()
        };

        if (report.TotalMismatchDocuments > 0)
        {
            _log.Warn(
                $"{report.TotalMismatchDocuments} document(s) on {businessDate} have tax rows that do not add " +
                "up to their printed total; the day report follows the tax rows.");
        }

        _log.Info(
            $"Day report for {businessDate}: {report.Documents} document(s), gross {Money(report.Gross)} " +
            $"{string.Join("/", report.Currencies)}, {lines.Count} line(s), " +
            $"{withoutDetail.Count} without a printed copy, " +
            $"{heldForReview + rejected + unknown + inFlight} not fiscalised.");

        return report;
    }

    private static void AddCurrency(SortedSet<string> currencies, string? currency)
    {
        if (!string.IsNullOrWhiteSpace(currency))
        {
            currencies.Add(currency.Trim());
        }
    }

    /// <summary>
    /// The bucket a printed line belongs to. The parser's marker gives the ZIMRA tax type; without
    /// one, the rate the line carries is the only statement of it and is reported as a rate.
    /// </summary>
    private static string LineTaxType(ReceiptPrintLine item) =>
        TrimToNull(item.TaxType) ?? (item.TaxRate is null or 0m
            ? "UNTYPED"
            : item.TaxRate.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%");

    private static long Min(long? current, long value) => current is null ? value : Math.Min(current.Value, value);

    private static long Max(long? current, long value) => current is null ? value : Math.Max(current.Value, value);

    private static int Min(int? current, int value) => current is null ? value : Math.Min(current.Value, value);

    private static int Max(int? current, int value) => current is null ? value : Math.Max(current.Value, value);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Who issued the documents, read off the receipts themselves rather than the config.</summary>
    private sealed class DocumentIdentity
    {
        public string? Company { get; private set; }
        public string? Tin { get; private set; }
        public string? VatNumber { get; private set; }
        public string? DeviceId { get; private set; }
        public string? DeviceSerialNo { get; private set; }
        public int? FiscalDayNo { get; set; }

        public void Take(ReceiptPrintModel model)
        {
            Company ??= TrimToNull(model.CompanyName);
            Tin ??= TrimToNull(model.CompanyTin);
            VatNumber ??= TrimToNull(model.CompanyVatNumber);
            DeviceId ??= TrimToNull(model.DeviceId);
            DeviceSerialNo ??= TrimToNull(model.DeviceSerialNo);
        }
    }

    /// <summary>
    /// Sums one kind of document into tax buckets. The rows are the ones printed on the document,
    /// so a report and its receipts say the same thing; where the gateway returned magnitudes for a
    /// correction, the rows are negated to agree with the document instead of adding to the day's VAT.
    /// </summary>
    private sealed class KindAccumulator
    {
        private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

        public int Documents { get; set; }
        public int WithDetail { get; set; }
        public decimal Net { get; set; }
        public decimal Tax { get; set; }
        public decimal Gross { get; set; }
        public decimal GrossWithoutDetail { get; set; }
        public int TotalMismatch { get; set; }

        public void Add(ReceiptPrintModel model)
        {
            WithDetail++;
            var rows = BucketRows(model);

            foreach (var row in rows)
            {
                if (!_buckets.TryGetValue(row.Label, out var bucket))
                {
                    bucket = new Bucket(row.Label, row.Rate, row.Exempt);
                    _buckets[row.Label] = bucket;
                }

                bucket.Documents++;
                bucket.Net += row.Net;
                bucket.Tax += row.Tax;
                bucket.Gross += row.Gross;
            }

            Net += rows.Sum(row => row.Net);
            Tax += rows.Sum(row => row.Tax);
            Gross += rows.Sum(row => row.Gross);

            if (Math.Abs(rows.Sum(row => row.Gross) - model.Total) > 0.05m)
            {
                TotalMismatch++;
            }
        }

        public FiscalDayKind ToKind() => new()
        {
            Documents = Documents,
            WithItemDetail = WithDetail,
            Net = Round(Net),
            Tax = Round(Tax),
            Gross = Round(Gross),
            GrossWithoutItemDetail = Round(GrossWithoutDetail),
            TotalMismatch = TotalMismatch,
            Buckets = _buckets.Values
                .OrderBy(bucket => bucket.Order)
                .ThenByDescending(bucket => bucket.Rate ?? 0m)
                .Select(bucket => new FiscalDayBucket(
                    bucket.Label, bucket.Documents, Round(bucket.Net), Round(bucket.Tax), Round(bucket.Gross)))
                .ToList()
        };

        private static List<Row> BucketRows(ReceiptPrintModel model)
        {
            if (model.Taxes.Count == 0)
            {
                // A document with no tax rows still took money, and a day report that dropped it
                // would understate the till against the gateway.
                var label = model.Items
                    .Select(item => TrimToNull(item.TaxType))
                    .FirstOrDefault(type => type is not null) ?? "NO TAX ROWS";
                return new List<Row>
                {
                    new(label, null, string.Equals(label, "EXEMPT", StringComparison.OrdinalIgnoreCase),
                        model.Subtotal, model.TaxTotal, model.Total)
                };
            }

            var rows = model.Taxes
                .Select(tax => new Row(
                    BucketLabel(tax),
                    tax.Rate,
                    tax.IsExempt,
                    tax.Net,
                    tax.Tax,
                    tax.Gross))
                .ToList();

            return ReconcileSign(rows, model);
        }

        private static List<Row> ReconcileSign(List<Row> rows, ReceiptPrintModel model)
        {
            if (model.Total >= 0m || rows.Sum(row => row.Gross) <= 0m)
            {
                return rows;
            }

            // The document was issued as a negative sale but its rows came back as magnitudes.
            return rows
                .Select(row => row with { Rate = -row.Rate, Net = -row.Net, Tax = -row.Tax, Gross = -row.Gross })
                .ToList();
        }

        private static string BucketLabel(ReceiptPrintTax tax) =>
            tax.IsExempt
                ? "EXEMPT"
                : tax.Rate is null
                    ? "TAX"
                    : tax.Rate == 0m
                        ? "ZERO RATED"
                        : tax.Rate.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

        private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private sealed record Row(string Label, decimal? Rate, bool Exempt, decimal Net, decimal Tax, decimal Gross);

        private sealed class Bucket
        {
            public Bucket(string label, decimal? rate, bool exempt)
            {
                Label = label;
                Rate = rate;
                Exempt = exempt;
            }

            public string Label { get; }
            public decimal? Rate { get; }
            public bool Exempt { get; }
            public int Documents { get; set; }
            public decimal Net { get; set; }
            public decimal Tax { get; set; }
            public decimal Gross { get; set; }

            /// <summary>Rated buckets first, then zero-rated, then exempt: the order of a Z-report.</summary>
            public int Order => Exempt ? 2 : Rate is null or 0m ? 1 : 0;
        }
    }
}
