namespace PrintInterceptor.Core.Parsing;

public enum ParseStatus
{
    Parsed,
    NeedsReview
}

public sealed record ParsedItem(
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    string TaxType,
    decimal TaxRate);

public sealed class ParsedBuyer
{
    public string? Name { get; init; }
    public string? VatNumber { get; init; }
    public string? Tin { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(VatNumber) &&
        string.IsNullOrWhiteSpace(Tin) &&
        string.IsNullOrWhiteSpace(Email) &&
        string.IsNullOrWhiteSpace(Phone) &&
        string.IsNullOrWhiteSpace(Address);
}

public sealed class ParsedReceipt
{
    public required IReadOnlyList<ParsedItem> Items { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string Currency { get; init; } = "USD";
    public string PaymentMethod { get; init; } = "CASH";
    public string TransactionType { get; init; } = "FiscalInvoice";
    public string? RelatedInvoiceNumber { get; init; }
    public string? CreditNoteReason { get; init; }
    public ParsedBuyer Buyer { get; init; } = new();
    public decimal Total { get; init; }
    public decimal TaxTotal { get; init; }
    public string SourceText { get; init; } = "";

    /// <summary>Business date used to scope invoice-number duplicate checks.</summary>
    public string BusinessDateKey =>
        (Date ?? DateTimeOffset.Now).ToString("yyyyMMdd");
}

public sealed record ParseResult(
    ParseStatus Status,
    ParsedReceipt? Receipt,
    IReadOnlyList<string> Warnings,
    string? Reason)
{
    public static ParseResult Review(string reason, IEnumerable<string>? warnings = null) =>
        new(ParseStatus.NeedsReview, null, warnings?.ToList() ?? new List<string>(), reason);

    public static ParseResult Success(ParsedReceipt receipt, IEnumerable<string> warnings) =>
        new(ParseStatus.Parsed, receipt, warnings.ToList(), null);
}
