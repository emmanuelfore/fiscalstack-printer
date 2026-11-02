using System.Globalization;

namespace PrintInterceptor.Core.Fiscalisation;

/// <summary>
/// A fiscalised invoice as the tenant's own invoice list returns it. These reads carry the
/// proof the till needs — fiscal code, QR data, ZIMRA counters — but not the verification
/// code, which only the fiscalise response includes. A receipt adopted from the list is
/// therefore printed without one.
/// </summary>
public sealed record InvoiceRecord(
    int Id,
    string InvoiceNumber,
    string? Status,
    string? TransactionType,
    string? Currency,
    decimal? Total,
    string? FiscalCode,
    string? QrCode,
    int? ReceiptGlobalNo,
    int? ReceiptCounter,
    int? FiscalDayNo,
    string? IssueDate)
{
    /// <summary>Re-shapes the record into the response the print pipeline already understands.</summary>
    public FiscalizeResponse ToResponse()
    {
        DateTimeOffset? receiptDate = null;
        if (DateTimeOffset.TryParse(
                IssueDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            receiptDate = parsed;
        }

        return new FiscalizeResponse
        {
            Success = true,
            FiscalCode = FiscalCode,
            QrCode = QrCode,
            ReceiptNumber = ReceiptGlobalNo,
            Receipt = new FiscalReceipt
            {
                InvoiceNo = InvoiceNumber,
                ReceiptType = TransactionType,
                ReceiptCurrency = Currency,
                ReceiptTotal = Total,
                ReceiptCounter = ReceiptCounter,
                ReceiptGlobalNo = ReceiptGlobalNo,
                FiscalDayNo = FiscalDayNo,
                ReceiptDate = receiptDate
            }
        };
    }
}

public enum InvoiceLookupStatus
{
    /// <summary>The invoice is on the server.</summary>
    Found,

    /// <summary>The list was read to its end without the number appearing.</summary>
    Absent,

    /// <summary>The list could not be read. Never treated as absent: a read error must not trigger a resend.</summary>
    Failed
}

/// <summary>
/// The answer to "did this receipt make it into ZIMRA?". <see cref="PagesRead"/> lets the
/// caller tell a complete walk from one that stopped at the configured window.
/// </summary>
/// <param name="WindowExhausted">
/// True when an absent answer came from reaching the page limit rather than the end of the
/// list, so the receipt could still exist outside the window that was read.
/// </param>
public sealed record InvoiceLookup(
    InvoiceLookupStatus Status,
    InvoiceRecord? Invoice,
    string? Error,
    int PagesRead,
    bool WindowExhausted = false);
