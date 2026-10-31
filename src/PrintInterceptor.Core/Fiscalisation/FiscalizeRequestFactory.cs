using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;

namespace PrintInterceptor.Core.Fiscalisation;

public static class FiscalizeRequestFactory
{
    public static FiscalizeRequest Create(ParsedReceipt receipt, ParseConfig config)
    {
        var taxInclusive = string.Equals(config.VatFlag.Trim(), "1", StringComparison.Ordinal);

        var request = new FiscalizeRequest
        {
            InvoiceNumber = string.IsNullOrWhiteSpace(receipt.InvoiceNumber) ? null : receipt.InvoiceNumber,
            Date = receipt.Date?.ToString("o"),
            Currency = receipt.Currency,
            PaymentMethod = receipt.PaymentMethod,
            TransactionType = receipt.TransactionType,
            RelatedInvoiceNumber = string.IsNullOrWhiteSpace(receipt.RelatedInvoiceNumber)
                ? null
                : receipt.RelatedInvoiceNumber,
            CreditNoteReason = string.IsNullOrWhiteSpace(receipt.CreditNoteReason)
                ? null
                : receipt.CreditNoteReason,
            Items = receipt.Items.Select(item => new FiscalizeItem
            {
                Name = item.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                TaxType = item.TaxType,
                TaxInclusive = taxInclusive
            }).ToList()
        };

        if (receipt.TransactionType == "CreditNote" && request.CreditNoteReason is null)
        {
            request.CreditNoteReason = "Credit note";
        }

        var buyer = new FiscalizeBuyer
        {
            RegisteredName = Clean(receipt.Buyer.Name),
            VatNumber = Clean(receipt.Buyer.VatNumber),
            Tin = Clean(receipt.Buyer.Tin),
            Email = Clean(receipt.Buyer.Email),
            Phone = Clean(receipt.Buyer.Phone),
            Street = Clean(receipt.Buyer.Address)
        };

        if (buyer.RegisteredName is not null ||
            buyer.VatNumber is not null ||
            buyer.Tin is not null ||
            buyer.Email is not null ||
            buyer.Phone is not null ||
            buyer.Street is not null)
        {
            request.Buyer = buyer;
        }

        return request;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length switch
        {
            0 => null,
            > 100 => trimmed[..100],
            _ => trimmed
        };
    }
}
