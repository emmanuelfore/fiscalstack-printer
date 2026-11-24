using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class PalladiumProfileTests
{
    private static ParseConfig Config()
    {
        var store = new ParseProfileStore(
            Path.Combine(AppContext.BaseDirectory, "profiles"),
            Path.Combine(Path.GetTempPath(), "palladium-profile-tests"));
        var profile = store.Find("palladium") ?? throw new InvalidOperationException("The Palladium profile did not load.");
        var config = new ParseConfig();
        profile.Rules.ApplyTo(config);
        return config;
    }

    private const string Invoice = """
        Fiscal Tax Invoice
        Number:IN-000000027
        Date:10/16/2026
        Account Type :ZWG
        Buyer Details:Bank Details:
        Customer Name       :Sample Inv                                                                   Account Name        :EXIMIUM PREMIUM BRANDS PVT LTD
        Customer Address    :::, Zimbabwe                                                                Bank                : CBZ Borrowdale Branch
        Customer TIN        :                                                                             Account Type        : USD
        TIN: 2002533710  VAT: 220451470
        Line      HS Code      Item              Description          Quantity      Unit      Price Inc      VAT      Total Inc
         1       85434000      VOL018BLU         V18X-BLUEBERRY              1         EA          1.00        0.16          1.00
        Subtotal:USD 1.00
        VAT Total :USD 0.16
        Invoice Total:USD 1.16
        Invoiced Currency:USD
        """;

    private const string CreditNote = """
        Fiscal Credit Note
        Number:CN-000000001
        Date:10/16/2026
        Reference:REV-IN-000000027
        Reason :DAMAGED
        Buyer Details:Bank Details:
        Customer Name:Sample Inv
        LineHS CodeItemDescriptionQuantityUnitVATTotal Inc
         185434000VOL018BLUV18X-BLUEBERRY-1EA 1.00-0.16-1.00
        Subtotal:-USD 1.00
        VAT Total :-USD 0.16
        Invoice Total:-USD 1.16
        Invoiced Currency:USD
        """;

    [Fact]
    public void ParsesPalladiumTaxInvoicePdfLayout()
    {
        var result = new ReceiptParser(Config()).Parse(Invoice);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("IN-000000027", receipt.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 10, 16), receipt.Date!.Value.Date);
        Assert.Equal("USD", receipt.Currency);
        Assert.Equal("Sample Inv", receipt.Buyer.Name);
        Assert.Single(receipt.Items);
        Assert.StartsWith("VOL018BLU", receipt.Items[0].Name);
        Assert.Contains("V18X-BLUEBERRY", receipt.Items[0].Name);
        Assert.DoesNotContain("85434000", receipt.Items[0].Name);
        Assert.Equal(1m, receipt.Items[0].Quantity);
        Assert.Equal(1.16m, receipt.Items[0].Amount);
        Assert.Equal(0.155m, receipt.Items[0].TaxRate);
        Assert.Equal(1.16m, receipt.Total);
        Assert.Equal(0.16m, receipt.TaxTotal);
    }

    [Fact]
    public void ParsesPalladiumCreditNotePdfLayout()
    {
        var result = new ReceiptParser(Config()).Parse(CreditNote);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("CN-000000001", receipt.InvoiceNumber);
        Assert.Equal("CreditNote", receipt.TransactionType);
        Assert.Equal("REV-IN-000000027", receipt.RelatedInvoiceNumber);
        Assert.Equal("DAMAGED", receipt.CreditNoteReason);
        Assert.Single(receipt.Items);
        Assert.Equal(-1m, receipt.Items[0].Quantity);
        Assert.Equal(-1.16m, receipt.Items[0].Amount);
        Assert.Equal(0.155m, receipt.Items[0].TaxRate);
        Assert.Equal(-1.16m, receipt.Total);
    }

    [Fact]
    public void TheVatAmountIsSentAsAnInclusivePriceNotAsAnApiRateField()
    {
        foreach (var text in new[] { Invoice, CreditNote })
        {
            var result = new ReceiptParser(Config()).Parse(text);
            var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);

            var request = FiscalizeRequestFactory.Create(receipt, Config());
            var json = JsonSerializer.Serialize(request, FiscalStackClient.RequestJsonOptions);

            // The paper's 1.00 + 0.16 becomes one inclusive 1.16 price. The gateway backs
            // the VAT out at the company rate; no rate or tax amount travels with it.
            Assert.Contains("\"quantity\":1", json);
            Assert.Contains("\"unitPrice\":1.16", json);
            Assert.Contains("\"taxInclusive\":true", json);
            Assert.DoesNotContain("taxRate", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("taxAmount", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ThePricingBasisCanBeSetToExclusive()
    {
        var config = Config();
        config.VatInclusive = false;

        var result = new ReceiptParser(config).Parse(Invoice);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);

        // The paper still balances at 1.16, but the ex-VAT column is 1.00. With an
        // exclusive basis the gateway gets that 1.00 and adds VAT itself.
        Assert.NotNull(receipt.Items[0].ExclusiveUnitPrice);
        Assert.Equal(1.00m, receipt.Items[0].ExclusiveUnitPrice!.Value);

        var request = FiscalizeRequestFactory.Create(receipt, config);
        Assert.Equal(1.00m, request.Items[0].UnitPrice);
        Assert.False(request.Items[0].TaxInclusive);
    }
}
