using System.Globalization;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;

namespace PrintInterceptor.Core.Fiscalisation;

/// <summary>
/// Builds the pass-through body. The gateway validates it with a strict schema, so a
/// field is either a value it accepts or absent: an unknown enum string, a null where
/// an optional was expected, or a non-positive quantity is a 400 that rejects the sale.
/// Anything we cannot map is omitted so the gateway's own default applies instead of a
/// guess, and the identifiers ZIMRA enforces a length on are dropped rather than truncated.
/// </summary>
public static class FiscalizeRequestFactory
{
    public static FiscalizeRequest Create(ParsedReceipt receipt, ParseConfig config)
    {
        var taxInclusive = string.Equals(config.VatFlag.Trim(), "1", StringComparison.Ordinal);
        var transactionType = TransactionType(receipt.TransactionType);

        var request = new FiscalizeRequest
        {
            InvoiceNumber = Trim(receipt.InvoiceNumber),
            Date = receipt.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Currency = Currency(receipt.Currency),
            PaymentMethod = PaymentMethod(receipt.PaymentMethod),
            TransactionType = transactionType,
            RelatedInvoiceNumber = Trim(receipt.RelatedInvoiceNumber),
            CreditNoteReason = Trim(receipt.CreditNoteReason),
            Items = receipt.Items
                .Select(item => new FiscalizeItem
                {
                    Name = item.Name.Trim(),
                    // A correction is signed by the gateway from transactionType, and the
                    // schema rejects a negative price or quantity outright.
                    Quantity = Math.Abs(item.Quantity),
                    UnitPrice = Math.Abs(item.UnitPrice),
                    TaxType = TaxType(item.TaxType),
                    TaxInclusive = taxInclusive
                })
                .ToList(),
            Buyer = Buyer(receipt.Buyer)
        };

        if (transactionType is "CreditNote" or "DebitNote" && request.CreditNoteReason is null)
        {
            request.CreditNoteReason = transactionType == "CreditNote" ? "Credit note" : "Debit note";
        }

        return request;
    }

    private static string? Currency(string? value)
    {
        var code = (value ?? "").Trim().ToUpperInvariant();
        return code switch
        {
            "USD" or "US$" => "USD",
            "ZWG" or "ZWL" or "RTGS" => "ZWG",
            // The pass-through route takes any ISO 4217 code, so a loti till states its
            // own money instead of falling back to the gateway default.
            "LSL" or "M" or "LS" => "LSL",
            _ => null
        };
    }

    private static string? PaymentMethod(string? value)
    {
        var code = (value ?? "").Trim().ToUpperInvariant();
        return code is "CASH" or "CARD" or "MOBILE" or "TRANSFER" ? code : null;
    }

    private static string? TransactionType(string? value)
    {
        var code = (value ?? "").Trim().ToUpperInvariant();
        return code switch
        {
            "FISCALINVOICE" => "FiscalInvoice",
            "CREDITNOTE" => "CreditNote",
            "DEBITNOTE" => "DebitNote",
            _ => null
        };
    }

    private static string? TaxType(string? value)
    {
        var code = (value ?? "").Trim().ToUpperInvariant();
        return code is "STANDARD" or "ZERO_RATED" or "EXEMPT" ? code : null;
    }

    private static FiscalizeBuyer? Buyer(ParsedBuyer parsed)
    {
        // ZIMRA only receives a buyer when the customer has a TIN; a name-only buyer
        // is rejected on the till, so the block is left off rather than sent half-empty.
        var tin = Digits(parsed.Tin);
        if (tin is not { Length: 10 })
        {
            return null;
        }

        var vat = Digits(parsed.VatNumber);
        var phone = SingleLine(parsed.Phone);
        var email = Email(parsed.Email);
        var name = Limit(Trim(parsed.Name), 100);

        return new FiscalizeBuyer
        {
            RegisteredName = name,
            Tin = tin,
            VatNumber = vat is { Length: 9 } ? vat : null,
            Email = email,
            Phone = phone is not null and { Length: <= 20 } ? phone : null,
            Street = Limit(Trim(parsed.Address), 100)
        };
    }

    /// <summary>The gateway rejects an email it cannot parse, which would reject the sale with it.</summary>
    private static string? Email(string? value)
    {
        var candidate = SingleLine(value);
        if (candidate is null || candidate.Length > 100 || !EmailPattern.IsMatch(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? Digits(string? value)
    {
        var chars = (value ?? "").Where(char.IsAsciiDigit).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }

    private static string? SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string? Trim(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Limit(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max].TrimEnd();
    }
}
