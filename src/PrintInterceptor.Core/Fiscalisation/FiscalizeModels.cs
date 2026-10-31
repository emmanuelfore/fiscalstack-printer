using System.Text.Json.Serialization;

namespace PrintInterceptor.Core.Fiscalisation;

public sealed class FiscalizeRequest
{
    [JsonPropertyName("items")]
    public List<FiscalizeItem> Items { get; set; } = new();

    [JsonPropertyName("buyer")]
    public FiscalizeBuyer? Buyer { get; set; }

    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = "CASH";

    [JsonPropertyName("transactionType")]
    public string TransactionType { get; set; } = "FiscalInvoice";

    [JsonPropertyName("relatedInvoiceNumber")]
    public string? RelatedInvoiceNumber { get; set; }

    [JsonPropertyName("creditNoteReason")]
    public string? CreditNoteReason { get; set; }

    [JsonPropertyName("offlineSignature")]
    public string? OfflineSignature { get; set; }

    [JsonPropertyName("offlineReceiptCounter")]
    public int? OfflineReceiptCounter { get; set; }

    [JsonPropertyName("offlineGlobalReceiptCounter")]
    public int? OfflineGlobalReceiptCounter { get; set; }

    [JsonPropertyName("offlinePreviousHash")]
    public string? OfflinePreviousHash { get; set; }

    [JsonPropertyName("offlineFiscalDay")]
    public int? OfflineFiscalDay { get; set; }

    [JsonPropertyName("offlineDate")]
    public string? OfflineDate { get; set; }
}

public sealed class FiscalizeItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("taxType")]
    public string TaxType { get; set; } = "STANDARD";

    /// <summary>True when unitPrice already contains VAT (VatFlag = 1).</summary>
    [JsonPropertyName("taxInclusive")]
    public bool TaxInclusive { get; set; } = true;
}

public sealed class FiscalizeBuyer
{
    [JsonPropertyName("registeredName")]
    public string? RegisteredName { get; set; }

    [JsonPropertyName("vatNumber")]
    public string? VatNumber { get; set; }

    [JsonPropertyName("tin")]
    public string? Tin { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("street")]
    public string? Street { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("province")]
    public string? Province { get; set; }
}

public sealed class FiscalizeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("fiscalCode")]
    public string? FiscalCode { get; set; }

    [JsonPropertyName("qrCode")]
    public string? QrCode { get; set; }

    [JsonPropertyName("verificationCode")]
    public string? VerificationCode { get; set; }

    [JsonPropertyName("receiptNumber")]
    public long? ReceiptNumber { get; set; }

    [JsonPropertyName("receipt")]
    public FiscalReceipt? Receipt { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class FiscalReceipt
{
    [JsonPropertyName("invoiceNo")]
    public string? InvoiceNo { get; set; }

    [JsonPropertyName("receiptDate")]
    public DateTimeOffset? ReceiptDate { get; set; }

    [JsonPropertyName("receiptType")]
    public string? ReceiptType { get; set; }

    [JsonPropertyName("receiptCurrency")]
    public string? ReceiptCurrency { get; set; }

    [JsonPropertyName("receiptTotal")]
    public decimal? ReceiptTotal { get; set; }

    [JsonPropertyName("receiptCounter")]
    public int? ReceiptCounter { get; set; }

    [JsonPropertyName("receiptGlobalNo")]
    public int? ReceiptGlobalNo { get; set; }

    [JsonPropertyName("fiscalDayNo")]
    public int? FiscalDayNo { get; set; }

    [JsonPropertyName("receiptPrintForm")]
    public string? ReceiptPrintForm { get; set; }

    [JsonPropertyName("receiptLines")]
    public List<FiscalReceiptLine>? ReceiptLines { get; set; }

    [JsonPropertyName("receiptTaxes")]
    public List<FiscalReceiptTax>? ReceiptTaxes { get; set; }

    [JsonPropertyName("receiptPayments")]
    public List<FiscalReceiptPayment>? ReceiptPayments { get; set; }

    [JsonPropertyName("buyerData")]
    public FiscalReceiptBuyer? BuyerData { get; set; }
}

public sealed class FiscalReceiptLine
{
    [JsonPropertyName("receiptLineName")]
    public string? ReceiptLineName { get; set; }

    [JsonPropertyName("receiptLineQuantity")]
    public decimal? ReceiptLineQuantity { get; set; }

    [JsonPropertyName("receiptLinePrice")]
    public decimal? ReceiptLinePrice { get; set; }

    [JsonPropertyName("receiptLineTotal")]
    public decimal? ReceiptLineTotal { get; set; }
}

public sealed class FiscalReceiptTax
{
    [JsonPropertyName("taxPercent")]
    public decimal? TaxPercent { get; set; }

    [JsonPropertyName("taxAmount")]
    public decimal? TaxAmount { get; set; }

    [JsonPropertyName("salesAmountWithTax")]
    public decimal? SalesAmountWithTax { get; set; }
}

public sealed class FiscalReceiptPayment
{
    [JsonPropertyName("moneyTypeCode")]
    public string? MoneyTypeCode { get; set; }

    [JsonPropertyName("paymentAmount")]
    public decimal? PaymentAmount { get; set; }
}

public sealed class FiscalReceiptBuyer
{
    [JsonPropertyName("buyerRegisterName")]
    public string? BuyerRegisterName { get; set; }

    [JsonPropertyName("vatNumber")]
    public string? VatNumber { get; set; }

    [JsonPropertyName("buyerTIN")]
    public string? BuyerTin { get; set; }
}
