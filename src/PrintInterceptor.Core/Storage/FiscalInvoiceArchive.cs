using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor.Core.Storage;

/// <summary>
/// Permanent, operator-readable copy of every issued fiscal invoice. Files are
/// grouped by local calendar day and stored as text for inspection plus JSON for
/// a lossless machine-readable record. Unlike captured jobs, these are fiscal
/// records and are deliberately not removed by the normal retention cleanup.
/// </summary>
public sealed class FiscalInvoiceArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly ILog _log;

    public FiscalInvoiceArchive(string dataDirectory, ILog log)
    {
        _root = Path.Combine(dataDirectory, "fiscalised");
        _log = log;
    }

    public string DirectoryPath => _root;

    public bool Save(string hash, ReceiptPrintModel model)
    {
        try
        {
            var directory = Path.Combine(_root, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);

            var stem = $"{SafeFilePart(model.InvoiceNumber)}-{Short(hash)}";
            var textPath = Path.Combine(directory, stem + ".txt");
            var jsonPath = Path.Combine(directory, stem + ".json");

            WriteAtomically(textPath, Render(model));
            WriteAtomically(jsonPath, JsonSerializer.Serialize(model, JsonOptions));
            _log.Info($"Archived {(model.IsOffline ? "offline " : "")}fiscal invoice: {textPath}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not archive the fiscal invoice for {Short(hash)}", ex);
            return false;
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string Render(ReceiptPrintModel model)
    {
        var text = new StringBuilder();
        text.AppendLine("FISCAL INVOICE ARCHIVE");
        text.AppendLine("======================");
        if (model.IsOffline)
        {
            text.AppendLine("Status: SIGNED OFFLINE - queued for FiscalStack synchronisation");
        }

        text.AppendLine();
        Append(text, "Company", model.CompanyName);
        Append(text, "Company TIN", model.CompanyTin);
        Append(text, "Company VAT", model.CompanyVatNumber);
        Append(text, "Address", model.CompanyAddress);
        Append(text, "Phone", model.CompanyPhone);
        Append(text, "Email", model.CompanyEmail);

        text.AppendLine();
        Append(text, "Invoice", model.InvoiceNumber);
        Append(text, "Date", model.DateText);
        Append(text, "Transaction", model.TransactionType);
        Append(text, "Related invoice", model.RelatedInvoiceNumber);
        Append(text, "Payment", model.PaymentMethod);

        if (HasBuyer(model))
        {
            text.AppendLine();
            text.AppendLine("BUYER");
            Append(text, "Name", model.BuyerName);
            Append(text, "Address", model.BuyerAddress);
            Append(text, "TIN", model.BuyerTin);
            Append(text, "VAT", model.BuyerVatNumber);
            Append(text, "Email", model.BuyerEmail);
            Append(text, "Phone", model.BuyerPhone);
        }

        text.AppendLine();
        text.AppendLine("ITEMS");
        foreach (var item in model.Items)
        {
            text.AppendLine(
                $"{item.Name} | Qty {Number(item.Quantity, "0.####")} | " +
                $"Unit {Money(item.UnitPrice)} | Amount {Money(item.Amount)} {model.Currency} | " +
                $"Tax {item.TaxType ?? "-"} {Number(item.TaxRate, "0.##")}%");
        }

        text.AppendLine();
        text.AppendLine("TAXES");
        foreach (var tax in model.Taxes)
        {
            text.AppendLine(
                $"{(tax.IsExempt ? "EXEMPT" : Number(tax.Rate, "0.##") + "%")} | " +
                $"Net {Money(tax.Net)} | Tax {Money(tax.Tax)} | Gross {Money(tax.Gross)} {model.Currency}");
        }

        text.AppendLine();
        text.AppendLine($"Subtotal: {Money(model.Subtotal)} {model.Currency}");
        text.AppendLine($"Tax total: {Money(model.TaxTotal)} {model.Currency}");
        text.AppendLine($"TOTAL: {Money(model.Total)} {model.Currency}");

        text.AppendLine();
        text.AppendLine("FISCAL PROOF");
        Append(text, "Fiscal code / hash", model.FiscalCode);
        Append(text, "Verification code", model.VerificationCode);
        Append(text, "QR URL", model.QrCodeUrl);
        Append(text, "Verification URL", model.VerifyUrl);
        Append(text, "Device ID", model.DeviceId);
        Append(text, "Device serial", model.DeviceSerialNo);
        Append(text, "Receipt number", model.ReceiptNumber?.ToString(CultureInfo.InvariantCulture));
        Append(text, "Fiscal day", model.FiscalDayNo?.ToString(CultureInfo.InvariantCulture));
        Append(text, "Receipt counter", model.ReceiptCounter?.ToString(CultureInfo.InvariantCulture));
        Append(text, "Global receipt number", model.ReceiptGlobalNo?.ToString(CultureInfo.InvariantCulture));
        Append(text, "Notes", model.Notes);
        return text.ToString();
    }

    private static bool HasBuyer(ReceiptPrintModel model) =>
        !string.IsNullOrWhiteSpace(model.BuyerName) ||
        !string.IsNullOrWhiteSpace(model.BuyerTin) ||
        !string.IsNullOrWhiteSpace(model.BuyerVatNumber);

    private static void Append(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine($"{label}: {value}");
        }
    }

    private static string Money(decimal? value) => Number(value, "0.00");

    private static string Number(decimal? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "-";

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((string.IsNullOrWhiteSpace(value) ? "receipt" : value.Trim())
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray());
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];
}
