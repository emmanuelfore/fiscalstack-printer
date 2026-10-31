using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;

namespace PrintInterceptor.Core.Fiscalisation;

public sealed record OfflineTaxBucket(
    int TaxId,
    string TaxCode,
    decimal? TaxPercent,
    decimal TaxAmount,
    decimal SalesAmountWithTax);

public sealed record OfflineLine(
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal NetAmount);

public sealed class OfflineSignedReceipt
{
    public string DeviceId { get; init; } = "";
    public string ReceiptType { get; init; } = "FiscalInvoice";
    public string Currency { get; init; } = "USD";
    public string ReceiptDate { get; init; } = "";
    public int ReceiptCounter { get; init; }
    public int ReceiptGlobalNo { get; init; }
    public int FiscalDayNo { get; init; }
    public decimal ReceiptTotal { get; init; }
    public decimal TaxTotal { get; init; }
    public IReadOnlyList<OfflineTaxBucket> Taxes { get; init; } = Array.Empty<OfflineTaxBucket>();
    public IReadOnlyList<OfflineLine> Lines { get; init; } = Array.Empty<OfflineLine>();
    public string PreviousHash { get; init; } = "";
    public string StringToSign { get; init; } = "";
    public string Hash { get; init; } = "";
    public string Signature { get; init; } = "";
    public string VerificationCode { get; init; } = "";
    public string QrCodeUrl { get; init; } = "";
}

public sealed class OfflineSignRequest
{
    public required string DeviceId { get; init; }
    public required string ReceiptType { get; init; }
    public required string Currency { get; init; }
    public required string ReceiptDate { get; init; }
    public required int ReceiptCounter { get; init; }
    public required int ReceiptGlobalNo { get; init; }
    public required int FiscalDayNo { get; init; }
    public required string PreviousHash { get; init; }
    public required IReadOnlyList<ParsedItem> Items { get; init; }
    public required bool TaxInclusive { get; init; }
    public IReadOnlyList<OfflineTaxConfig> Taxes { get; init; } = OfflineConfig.DefaultTaxes();
    public decimal StandardTaxPercent { get; init; }
    public string QrBaseUrl { get; init; } = "https://fdms.zimra.co.zw/";
}

/// <summary>
/// Mirrors the ZIMRA device signature computed by the FiscalStack server
/// (server/lib/fiscal-signatures.ts and zimra.ts) so an offline receipt carries
/// the same hash, signature, verification code and QR URL.
/// </summary>
public static class OfflineSigner
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static OfflineSignedReceipt Sign(OfflineSignRequest request, RSA privateKey)
    {
        if (string.IsNullOrWhiteSpace(request.PreviousHash))
        {
            throw new InvalidOperationException("Offline signing requires the previous receipt hash.");
        }

        var mapping = BuildTaxMapping(request.Taxes, request.StandardTaxPercent);
        var receiptType = NormalizeReceiptType(request.ReceiptType);
        var sign = receiptType == "CreditNote" ? -1m : 1m;

        var buckets = new Dictionary<string, (int TaxId, string TaxCode, decimal Percent, decimal Net)>(StringComparer.Ordinal);
        var lines = new List<OfflineLine>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var tax = ResolveTax(mapping, item.TaxType);
            var unitPrice = item.UnitPrice;
            if (request.TaxInclusive && tax.Percent > 0m)
            {
                unitPrice /= 1m + tax.Percent / 100m;
            }

            var lineTotal = Round2(item.Quantity * Round4(unitPrice)) * sign;
            var key = $"{tax.TaxId}|{tax.Code}|{tax.Percent.ToString("0.00", Invariant)}";
            var bucket = buckets.TryGetValue(key, out var existing)
                ? existing
                : (TaxId: tax.TaxId, TaxCode: tax.Code, Percent: tax.Percent, Net: 0m);
            bucket.Net += lineTotal;
            buckets[key] = bucket;

            lines.Add(new OfflineLine(
                item.Name,
                item.Quantity,
                Round4(unitPrice),
                lineTotal));
        }

        var taxes = new List<OfflineTaxBucket>(buckets.Count);
        foreach (var bucket in buckets.Values)
        {
            var net = Round2(bucket.Net);
            var taxAmount = bucket.Percent > 0m ? Round2(net * bucket.Percent / 100m) : 0m;
            taxes.Add(new OfflineTaxBucket(
                bucket.TaxId,
                bucket.TaxCode,
                bucket.TaxId == 1 ? null : bucket.Percent,
                taxAmount,
                Round2(net + taxAmount)));
        }

        taxes.Sort((left, right) =>
        {
            var byId = left.TaxId.CompareTo(right.TaxId);
            return byId != 0 ? byId : string.CompareOrdinal(left.TaxCode, right.TaxCode);
        });

        var receiptTotal = Round2(taxes.Sum(tax => tax.SalesAmountWithTax));
        var taxTotal = Round2(taxes.Sum(tax => tax.TaxAmount));

        var taxesSignature = string.Concat(taxes.Select(tax =>
            $"{tax.TaxCode}{FormatPercent(tax.TaxId, tax.TaxPercent)}{ToCents(tax.TaxAmount)}{ToCents(tax.SalesAmountWithTax)}"));

        var stringToSign =
            NormalizeDeviceId(request.DeviceId) +
            receiptType.ToUpperInvariant() +
            request.Currency.ToUpperInvariant() +
            request.ReceiptGlobalNo.ToString(Invariant) +
            request.ReceiptDate +
            ToCents(receiptTotal) +
            taxesSignature +
            request.PreviousHash;

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(stringToSign)));
        var signature = Convert.ToBase64String(
            privateKey.SignData(Encoding.UTF8.GetBytes(stringToSign), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var verificationCode = VerificationCode(signature, dashed: true);
        var qrCodeUrl = BuildQrUrl(request.QrBaseUrl, request.DeviceId, request.ReceiptGlobalNo, request.ReceiptDate, signature);

        return new OfflineSignedReceipt
        {
            DeviceId = NormalizeDeviceId(request.DeviceId),
            ReceiptType = receiptType,
            Currency = request.Currency.ToUpperInvariant(),
            ReceiptDate = request.ReceiptDate,
            ReceiptCounter = request.ReceiptCounter,
            ReceiptGlobalNo = request.ReceiptGlobalNo,
            FiscalDayNo = request.FiscalDayNo,
            ReceiptTotal = receiptTotal,
            TaxTotal = taxTotal,
            Taxes = taxes,
            Lines = lines,
            PreviousHash = request.PreviousHash,
            StringToSign = stringToSign,
            Hash = hash,
            Signature = signature,
            VerificationCode = verificationCode,
            QrCodeUrl = qrCodeUrl
        };
    }

    public static string VerificationCode(string signatureBase64, bool dashed)
    {
        var signatureBytes = Convert.FromBase64String(signatureBase64);
        var md5 = Convert.ToHexString(MD5.HashData(signatureBytes));
        var code = md5[..16];
        return dashed
            ? $"{code[..4]}-{code[4..8]}-{code[8..12]}-{code[12..16]}"
            : code;
    }

    public static string BuildQrUrl(string baseUrl, string deviceId, int globalNo, string receiptDate, string signatureBase64)
    {
        var md5 = VerificationCode(signatureBase64, dashed: false);
        var device = NormalizeDeviceId(deviceId).PadLeft(10, '0');

        DateTime parsed;
        if (!DateTime.TryParse(receiptDate, Invariant, DateTimeStyles.None, out parsed))
        {
            parsed = DateTime.Now;
        }

        var qrDate = parsed.ToString("ddMMyyyy", Invariant);
        var global = globalNo.ToString(Invariant).PadLeft(10, '0');
        var prefix = string.IsNullOrWhiteSpace(baseUrl) ? "https://fdms.zimra.co.zw/" : baseUrl;
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return $"{prefix}{device}{qrDate}{global}{md5}";
    }

    public static string NormalizeDeviceId(string deviceId)
    {
        var digits = new string(deviceId.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return "0";
        }

        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    public static string FormatReceiptDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", Invariant);

    private static string FormatPercent(int taxId, decimal? percent) =>
        taxId == 1 || percent is null ? "" : percent.Value.ToString("0.00", Invariant);

    private static string ToCents(decimal value) =>
        ((long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero)).ToString(Invariant);

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string NormalizeReceiptType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "creditnote" => "CreditNote",
        "debitnote" => "DebitNote",
        _ => "FiscalInvoice"
    };

    private static (int TaxId, string Code, decimal Percent) ResolveTax(
        IReadOnlyDictionary<string, (int TaxId, string Code, decimal Percent)> mapping,
        string taxType)
    {
        var key = NormalizeTaxType(taxType);
        return mapping.TryGetValue(key, out var tax)
            ? tax
            : mapping["STANDARD"];
    }

    private static Dictionary<string, (int TaxId, string Code, decimal Percent)> BuildTaxMapping(
        IReadOnlyList<OfflineTaxConfig> taxes,
        decimal standardTaxPercent)
    {
        var mapping = new Dictionary<string, (int, string, decimal)>(StringComparer.Ordinal);
        foreach (var tax in taxes)
        {
            var type = NormalizeTaxType(tax.TaxType);
            // The live ZIMRA bucket percent wins; the configured value is only a
            // fallback for manually provisioned tax tables.
            var percent = type == "STANDARD"
                ? (tax.TaxPercent is { } live && live > 0 ? (decimal)live : standardTaxPercent)
                : 0m;
            mapping[type] = (tax.TaxId, string.IsNullOrWhiteSpace(tax.TaxCode) ? DefaultCode(tax.TaxId) : tax.TaxCode, percent);
        }

        mapping.TryAdd("STANDARD", (3, "A", standardTaxPercent));
        mapping.TryAdd("ZERO_RATED", (2, "B", 0m));
        mapping.TryAdd("EXEMPT", (1, "C", 0m));
        return mapping;
    }

    private static string DefaultCode(int taxId) => taxId switch
    {
        1 => "C",
        2 => "B",
        4 => "E",
        _ => "A"
    };

    private static string NormalizeTaxType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "ZERO" or "ZERORATED" or "ZERO-RATED" => "ZERO_RATED",
        "NONVAT" or "NON-VAT" or "NON_VAT" => "EXEMPT",
        _ => value.Trim().ToUpperInvariant()
    };
}
