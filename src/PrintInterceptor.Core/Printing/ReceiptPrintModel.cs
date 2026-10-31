using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Printing;

public enum PrintFormat
{
    Thermal,
    A4
}

public sealed record ReceiptPrintLine(
    string Name,
    decimal Quantity,
    decimal Amount,
    decimal? UnitPrice = null,
    decimal? TaxRate = null,
    string? TaxType = null,
    string? Code = null);

public sealed record ReceiptPrintTax(
    decimal? Rate,
    bool IsExempt,
    decimal Net,
    decimal Tax,
    decimal Gross);

public sealed class ReceiptPrintModel
{
    public PrintFormat Format { get; init; } = PrintFormat.Thermal;

    public string CompanyName { get; init; } = "";
    public string CompanyTin { get; init; } = "";
    public string CompanyVatNumber { get; init; } = "";
    public string CompanyAddress { get; init; } = "";
    public string CompanyPhone { get; init; } = "";
    public string CompanyEmail { get; init; } = "";
    public string? CompanyLogoPath { get; init; }

    public string InvoiceNumber { get; init; } = "";
    public string DateText { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public string PaymentMethod { get; init; } = "CASH";
    public string TransactionType { get; init; } = "FiscalInvoice";
    public string? RelatedInvoiceNumber { get; init; }
    public string? RelatedInvoiceDate { get; init; }

    public string? BuyerName { get; init; }
    public string? BuyerAddress { get; init; }
    public string? BuyerTin { get; init; }
    public string? BuyerVatNumber { get; init; }
    public string? BuyerEmail { get; init; }
    public string? BuyerPhone { get; init; }

    public IReadOnlyList<ReceiptPrintLine> Items { get; init; } = Array.Empty<ReceiptPrintLine>();
    public IReadOnlyList<ReceiptPrintTax> Taxes { get; init; } = Array.Empty<ReceiptPrintTax>();
    public decimal Total { get; init; }
    public decimal TaxTotal { get; init; }
    public decimal Subtotal { get; init; }
    public bool TaxInclusive { get; init; } = true;

    /// <summary>True when the signature was produced locally and is queued for sync.</summary>
    public bool IsOffline { get; init; }

    /// <summary>Set for operator reprints so the copy is marked as such.</summary>
    public bool IsReprint { get; set; }

    public string? FiscalCode { get; init; }
    public string? VerificationCode { get; init; }
    public string? QrCodeUrl { get; init; }
    public string? VerifyUrl { get; init; }
    public string DeviceId { get; init; } = "";
    public string DeviceSerialNo { get; init; } = "";
    public long? ReceiptNumber { get; init; }
    public int? ReceiptCounter { get; init; }
    public int? ReceiptGlobalNo { get; init; }
    public int? FiscalDayNo { get; init; }
    public string? Notes { get; init; }

    public string PrinterName { get; init; } = "";
    public string A4PrinterName { get; init; } = "";
    public int PaperWidthMm { get; init; } = 80;
    public int Copies { get; init; } = 1;
    public string AccentColor { get; init; } = "#2563EB";
    public string FooterText { get; init; } = "Thank you for your business!";

    /// <summary>A4 invoices go to their own printer; the receipt printer is the fallback.</summary>
    public string EffectivePrinterName =>
        Format == PrintFormat.A4 && !string.IsNullOrWhiteSpace(A4PrinterName)
            ? A4PrinterName
            : PrinterName;

    public bool IsCredit =>
        string.Equals(TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase);
}

public interface IReceiptPrinter
{
    void Print(ReceiptPrintModel model);
}

public sealed class NullReceiptPrinter : IReceiptPrinter
{
    private readonly ILog _log;

    public NullReceiptPrinter(ILog log)
    {
        _log = log;
    }

    public void Print(ReceiptPrintModel model)
    {
        _log.Warn("Receipt printing is not available on this host; fiscal receipt was not printed.");
    }
}

public static class QrCodeGenerator
{
    public static byte[] CreatePng(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data);
        return png.GetGraphic(8);
    }
}
