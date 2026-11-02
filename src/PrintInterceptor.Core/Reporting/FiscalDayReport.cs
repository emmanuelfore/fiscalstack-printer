using System.Globalization;
using System.Text;

namespace PrintInterceptor.Core.Reporting;

/// <summary>
/// One tax bucket of the day, as the documents that were actually issued state it.
/// </summary>
public sealed record FiscalDayBucket(string Label, int Documents, decimal Net, decimal Tax, decimal Gross);

/// <summary>
/// The sales, credit-note or debit-note side of the day. <see cref="Documents"/> is every
/// fiscalised document of the kind; the totals cover only those whose printed copy is still on the
/// till, which is why <see cref="GrossWithoutItemDetail"/> exists rather than a silent estimate.
/// </summary>
public sealed record FiscalDayKind
{
    public int Documents { get; init; }

    /// <summary>Documents whose printed fiscal copy was read, so the buckets and lines exist.</summary>
    public int WithItemDetail { get; init; }

    public decimal Net { get; init; }
    public decimal Tax { get; init; }
    public decimal Gross { get; init; }

    /// <summary>Gross of the fiscalised documents that could not be read back; not in the totals above.</summary>
    public decimal GrossWithoutItemDetail { get; init; }

    /// <summary>Documents whose own tax rows do not add up to their printed total.</summary>
    public int TotalMismatch { get; init; }

    public IReadOnlyList<FiscalDayBucket> Buckets { get; init; } = Array.Empty<FiscalDayBucket>();
}

/// <summary>
/// One printed line of a fiscalised document, carrying the fiscal proof it belongs to so the
/// accountant's spreadsheet can pivot by bucket, receipt number or fiscal code without joins.
/// </summary>
public sealed record FiscalDayLine(
    string BusinessDate,
    string InvoiceNumber,
    string TransactionType,
    string Line,
    decimal Quantity,
    decimal? UnitPrice,
    decimal LineTotal,
    string TaxType,
    decimal? TaxRatePercent,
    string? FiscalCode,
    long? ReceiptNumber,
    int? ReceiptCounter,
    int? ReceiptGlobalNo,
    int? FiscalDayNo,
    string? DeviceId,
    string? DeviceSerialNo,
    string Currency,
    bool IsOffline,
    string? BuyerName,
    string? BuyerTin);

/// <summary>
/// What one business date looks like from the till: the documents it fiscalised, their VAT buckets,
/// the fiscal receipts issued, and everything still outstanding. This is the Z-shift reading a
/// shopkeeper hands over and the basis of the accountant's export, so every number in it comes from
/// a document the till printed or a ledger entry the gateway answered.
/// </summary>
public sealed record FiscalDayReport
{
    private static readonly string[] CsvHeader =
    [
        "businessDate", "invoiceNumber", "transactionType", "receiptNumber", "receiptCounter",
        "receiptGlobalNo", "fiscalDayNo", "fiscalCode", "deviceId", "deviceSerialNo", "currency",
        "signedOffline", "buyerName", "buyerTin", "line", "quantity", "unitPrice", "lineTotal",
        "taxType", "taxRatePercent"
    ];

    public string BusinessDate { get; init; } = "";
    public string? CompanyName { get; init; }
    public string? CompanyTin { get; init; }
    public string? CompanyVatNumber { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceSerialNo { get; init; }
    public int? FiscalDayNo { get; init; }
    public long? FirstReceiptNumber { get; init; }
    public long? LastReceiptNumber { get; init; }
    public int? FirstCounter { get; init; }
    public int? LastCounter { get; init; }

    public FiscalDayKind Sales { get; init; } = new();
    public FiscalDayKind Credits { get; init; } = new();
    public FiscalDayKind Debits { get; init; } = new();

    /// <summary>Fiscalised while offline and still waiting for the queue to sync.</summary>
    public int WaitingForSync { get; init; }

    public int HeldForReview { get; init; }
    public int Rejected { get; init; }
    public int UnknownOutcome { get; init; }
    public int InFlight { get; init; }

    /// <summary>Issued documents with no printed copy left on this till, with the gross that is not in the totals.</summary>
    public IReadOnlyList<string> WithoutItemDetail { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FiscalDayLine> Lines { get; init; } = Array.Empty<FiscalDayLine>();

    public IReadOnlyList<string> Currencies { get; init; } = Array.Empty<string>();

    public int Documents => Sales.Documents + Credits.Documents + Debits.Documents;

    /// <summary>What the day adds up to at the till: sales plus the signed value of its corrections.</summary>
    public decimal Gross => Sales.Gross + Credits.Gross + Debits.Gross;

    public decimal Tax => Sales.Tax + Credits.Tax + Debits.Tax;

    /// <summary>Gross issued on the day but missing from the totals because its document cannot be read here.</summary>
    public decimal GrossWithoutItemDetail =>
        Sales.GrossWithoutItemDetail + Credits.GrossWithoutItemDetail + Debits.GrossWithoutItemDetail;

    public int TotalMismatchDocuments => Sales.TotalMismatch + Credits.TotalMismatch + Debits.TotalMismatch;

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"FISCAL DAY REPORT - {BusinessDate}");
        text.AppendLine(
            $"Generated {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} " +
            "from this till's own records.");
        text.AppendLine();

        Row(text, "Company", CompanyName);
        Row(text, "TIN", CompanyTin);
        Row(text, "VAT number", CompanyVatNumber);
        Row(
            text,
            "Device",
            DeviceId is null && DeviceSerialNo is null ? null : $"{DeviceId ?? "-"} (serial {DeviceSerialNo ?? "-"})");
        Row(text, "Fiscal day", Number(FiscalDayNo));
        Row(text, "Receipt numbers", ReceiptRange(FirstReceiptNumber, LastReceiptNumber));
        Row(text, "Receipt counters", ReceiptRange(FirstCounter, LastCounter));
        Row(text, "Currency", Currencies.Count == 0 ? null : string.Join(", ", Currencies));
        text.AppendLine();

        text.AppendLine(
            $"Documents issued: {Documents}  (sales {Sales.Documents}, credit notes {Credits.Documents}, " +
            $"debit notes {Debits.Documents})");
        if (WaitingForSync > 0)
        {
            text.AppendLine($"  of which signed on the till, waiting to sync: {WaitingForSync}");
        }

        text.AppendLine();

        KindTable(text, "SALES", Sales);
        KindTable(text, "CREDIT NOTES", Credits);
        KindTable(text, "DEBIT NOTES", Debits);

        text.AppendLine("DAY POSITION");
        text.AppendLine($"  Gross sales          {Amount(Sales.Gross),12}");
        if (Credits.Documents > 0)
        {
            text.AppendLine($"  Gross credit notes   {Amount(Credits.Gross),12}");
        }

        if (Debits.Documents > 0)
        {
            text.AppendLine($"  Gross debit notes    {Amount(Debits.Gross),12}");
        }

        text.AppendLine($"  Net for the day      {Amount(Gross),12}");
        text.AppendLine($"  Tax for the day      {Amount(Tax),12}");
        if (GrossWithoutItemDetail != 0m)
        {
            text.AppendLine($"  Issued, no copy    {Amount(GrossWithoutItemDetail),12}");
        }

        text.AppendLine();

        text.AppendLine("NOT FISCALISED FOR THIS DATE");
        text.AppendLine($"  Held for review      {HeldForReview,12}");
        text.AppendLine($"  Rejected             {Rejected,12}");
        text.AppendLine($"  Outcome unknown      {UnknownOutcome,12}");
        text.AppendLine($"  In flight            {InFlight,12}");

        var notes = Notes().ToList();
        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("NOTES");
            foreach (var note in notes)
            {
                text.AppendLine("  - " + note);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// One row per printed line of every fiscalised document, which is what an accountant
    /// reconciles against the VAT buckets. CRLF, because this file is opened in Excel on a till.
    /// Text is neutralised so a product name typed at the counter cannot arrive as a formula.
    /// </summary>
    public string Csv()
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", CsvHeader));
        foreach (var line in Lines)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(line.BusinessDate),
                CsvField(line.InvoiceNumber),
                CsvField(line.TransactionType),
                Number(line.ReceiptNumber),
                Number(line.ReceiptCounter),
                Number(line.ReceiptGlobalNo),
                Number(line.FiscalDayNo),
                CsvField(line.FiscalCode),
                CsvField(line.DeviceId),
                CsvField(line.DeviceSerialNo),
                CsvField(line.Currency),
                line.IsOffline ? "yes" : "no",
                CsvField(line.BuyerName),
                CsvField(line.BuyerTin),
                CsvField(line.Line),
                Decimal(line.Quantity),
                Decimal(line.UnitPrice),
                Decimal(line.LineTotal),
                CsvField(line.TaxType),
                Decimal(line.TaxRatePercent)
            }));
        }

        return csv.ToString();
    }

    private IEnumerable<string> Notes()
    {
        if (Documents == 0)
        {
            yield return
                "No fiscal document was issued for this date, so there is nothing to report.";
        }

        if (WithoutItemDetail.Count > 0)
        {
            yield return
                $"{WithoutItemDetail.Count} issued document(s) have no printed copy left on this till " +
                $"({string.Join(", ", WithoutItemDetail)}). Their tax buckets, lines and gross are not in " +
                "the totals or the CSV.";
        }

        if (Currencies.Count > 1)
        {
            yield return
                $"This date has documents in more than one currency ({string.Join(", ", Currencies)}); the " +
                "totals are added as printed, not converted.";
        }

        if (WaitingForSync > 0)
        {
            yield return
                $"{WaitingForSync} receipt(s) were signed on the till and are still waiting to sync. They are " +
                "real fiscal documents and are included above; the gateway's signature replaces the local one at sync.";
        }

        if (UnknownOutcome > 0)
        {
            yield return
                $"{UnknownOutcome} job(s) have an outcome this till could not read. They are not counted as " +
                "issued. Run Reconcile unknown receipts now from the tray before this day is reported as closed.";
        }

        if (HeldForReview > 0)
        {
            yield return
                $"{HeldForReview} job(s) were held for review and never fiscalised; those sales are not at ZIMRA " +
                "until the receipt is corrected and sent.";
        }

        if (Rejected > 0)
        {
            yield return $"{Rejected} job(s) were rejected by the gateway and are not fiscal documents.";
        }

        yield return
            "Totals are what this till printed and the gateway confirmed. FiscalStack holds the authoritative " +
            "copy of every document listed here.";
    }

    private static void KindTable(StringBuilder text, string title, FiscalDayKind kind)
    {
        if (kind.Documents == 0)
        {
            return;
        }

        text.AppendLine(
            $"{title} - {kind.Documents} document(s)" +
            (kind.WithItemDetail == kind.Documents ? "" : $", {kind.WithItemDetail} with a printed copy here"));
        text.AppendLine("  Bucket             Net          Tax        Gross");
        foreach (var bucket in kind.Buckets)
        {
            text.AppendLine(
                $"  {bucket.Label,-15} {Amount(bucket.Net),11} {Amount(bucket.Tax),11} {Amount(bucket.Gross),11}");
        }

        text.AppendLine($"  {"TOTAL",15} {Amount(kind.Net),11} {Amount(kind.Tax),11} {Amount(kind.Gross),11}");
        text.AppendLine();
    }

    private static void Row(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine($"{label + " ",-18}: {value}");
        }
    }

    private static string ReceiptRange<T>(T? first, T? last) where T : struct
    {
        if (first is null)
        {
            return "";
        }

        return Equals(first, last)
            ? Convert.ToString(first, CultureInfo.InvariantCulture) ?? ""
            : $"{Convert.ToString(first, CultureInfo.InvariantCulture)} to {Convert.ToString(last, CultureInfo.InvariantCulture)}";
    }

    private static string Amount(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Decimal(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    private static string CsvField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();

        // A cell that starts with an operator is evaluated by the spreadsheet, and line names and
        // buyer fields are typed at the counter. The apostrophe keeps the text and drops the formula.
        if (text[0] is '=' or '@' or '+' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }
}
