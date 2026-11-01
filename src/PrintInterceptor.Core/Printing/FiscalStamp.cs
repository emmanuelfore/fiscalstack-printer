using System.Globalization;

namespace PrintInterceptor.Core.Printing;

public sealed record FiscalStampLine(string Label, string Value);

/// <summary>
/// The fiscal proof that is stamped onto the POS's own invoice when
/// <c>printing.template</c> is <c>original</c>. Both stamp renderers (thermal and
/// A4) draw from this, so a till and a laser printer show identical proof.
///
/// The lines are the ZIMRA identifiers the POS could not know when it printed the
/// document; the totals and the buyer details are already on the invoice beneath
/// the stamp, so they are not repeated here.
/// </summary>
public static class FiscalStamp
{
    public const string Heading = "FISCAL PROOF";
    public const string ReprintHeading = "FISCAL PROOF - DUPLICATE COPY";

    public static string HeadingFor(ReceiptPrintModel model) =>
        model.IsReprint ? ReprintHeading : Heading;

    public static IReadOnlyList<FiscalStampLine> Lines(ReceiptPrintModel model)
    {
        var lines = new List<FiscalStampLine>
        {
            new("Invoice No", InvoiceNumber(model)),
            new("Date", DateOnly(model.DateText)),
            new("Customer Reference No", OrNa(model.InvoiceNumber)),
            new("Fiscal Device ID", OrNa(model.DeviceId)),
            new("Fiscal Day No", Number(model.FiscalDayNo)),
            new("Device Serial No", OrNa(model.DeviceSerialNo))
        };

        // The QR carries the fiscal signature; without one the code has to be read
        // by a verifier, so it is stamped as text instead.
        if (QrPayload(model) is null && !string.IsNullOrWhiteSpace(model.FiscalCode))
        {
            lines.Add(new FiscalStampLine("Fiscal Code", model.FiscalCode!));
        }

        return lines;
    }

    /// <summary>
    /// The invoice number ZIMRA expects to see quoted is the fiscal receipt number,
    /// written as day-counter/global-number. FiscalStack returns the global number
    /// either inside the receipt block or as the top-level receipt number, and the
    /// POS's own document number becomes the customer reference.
    /// </summary>
    public static string InvoiceNumber(ReceiptPrintModel model)
    {
        var global = (long?)model.ReceiptGlobalNo ?? model.ReceiptNumber;
        if (global is null)
        {
            return OrNa(model.InvoiceNumber);
        }

        var globalNo = global.Value;
        return model.ReceiptCounter is { } counter
            ? $"{counter}/{globalNo.ToString(CultureInfo.InvariantCulture)}"
            : globalNo.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The stamp quotes the day like the invoice grid does, without a time.</summary>
    public static string DateOnly(string? dateText)
    {
        if (string.IsNullOrWhiteSpace(dateText))
        {
            return "N/A";
        }

        var space = dateText.IndexOf(' ');
        return space > 0 ? dateText[..space] : dateText;
    }

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "N/A";

    private static string OrNa(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

    /// <summary>The QR payload, plus the human-readable verification address.</summary>
    public static string? QrPayload(ReceiptPrintModel model) =>
        string.IsNullOrWhiteSpace(model.QrCodeUrl) ? null : model.QrCodeUrl;

    public static string? VerificationUrl(ReceiptPrintModel model) =>
        string.IsNullOrWhiteSpace(model.VerifyUrl) ? null : model.VerifyUrl;
}
