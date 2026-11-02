using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// A return is the one document that can quietly file VAT on money that went back to the
/// customer, so the parser's whole correction path is covered here: which printed words type
/// a document, which numbers are read as the original it reverses, and which slips are held
/// because the paper does not say enough to file them.
/// </summary>
public class ReturnsAndCorrectionsTests
{
    private static ParseConfig Config() => new()
    {
        ProductStartLine = "Order No.",
        ProductEndLine = "Items count",
        InvoiceNumber = "Order No.",
        CustomerName = "Buyer Name",
        CustomerTIN = "Buyer TIN",
        CustomerVATNumber = "Buyer VAT",
        Currency = "USD",
        PaymentMethod = "CASH",
        VatFlag = "1",
        VatRate = 0.15
    };

    private const string ReturnSlip = """
        Store Demo
        RETURN
        Order No.: CN-1001
        Buyer Name: Acme Ltd
        Original Invoice: INV-1001
        Return Reason: Wrong size
        Date: 29/10/2026 10:15
        --------------------------------
        Bread Loaf 1.00 2.50 2.50
        --------------------------------
        Items count: 1
        Total: 2.50
        Payment: CASH
        """;

    [Fact]
    public void AReturnAnnouncedAboveTheItemsIsFiledAsACreditNote()
    {
        var result = new ReceiptParser(Config()).Parse(ReturnSlip);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("CreditNote", receipt.TransactionType);
        Assert.Equal("CN-1001", receipt.InvoiceNumber);
        Assert.Equal("INV-1001", receipt.RelatedInvoiceNumber);
        Assert.Equal("Wrong size", receipt.CreditNoteReason);
    }

    [Fact]
    public void AReturnNumberedByItsPrefixIsFiledAsACreditNoteEvenWithoutTheWord()
    {
        var text = ReturnSlip
            .Replace("RETURN\n", "")
            .Replace("Order No.: CN-1001", "Order No.: CR-1001");

        var receipt = new ReceiptParser(Config()).Parse(text).Receipt;

        Assert.Equal("CreditNote", receipt!.TransactionType);
        Assert.Equal("CR-1001", receipt.InvoiceNumber);
    }

    [Fact]
    public void APrintedReturnsPolicyBelowTheItemsDoesNotTypeTheDocument()
    {
        const string text = """
            Store Demo
            Order No.: INV-1002
            --------------------------------
            Brass Return Coupling 1.00 2.50 2.50
            --------------------------------
            Items count: 1
            Total: 2.50
            NO RETURNS AFTER 7 DAYS. EXCHANGED GOODS ONLY.
            """;

        var receipt = new ReceiptParser(Config()).Parse(text).Receipt;

        Assert.Equal("FiscalInvoice", receipt!.TransactionType);
        Assert.Null(receipt.RelatedInvoiceNumber);
        Assert.Null(receipt.CreditNoteReason);
    }

    [Fact]
    public void ARefundSlipThatOnlyPrintsANegativeTotalIsFiledAsACreditNote()
    {
        const string text = """
            Store Demo
            Order No.: INV-1003
            Original Invoice: INV-0988
            --------------------------------
            Bread Loaf 1.00 -2.50 -2.50
            --------------------------------
            Items count: 1
            Total: -2.50
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("CreditNote", receipt.TransactionType);
        Assert.Equal(-2.50m, receipt.Items[0].Amount);
        Assert.Equal(-2.50m, receipt.Total);
        Assert.Equal("INV-0988", receipt.RelatedInvoiceNumber);
        Assert.Contains(result.Warnings, warning => warning.Contains("filed as a credit note"));
    }

    [Fact]
    public void ASaleAndAReturnOnTheSameSlipIsHeldRatherThanNetted()
    {
        const string text = """
            Store Demo
            Order No.: INV-1004
            --------------------------------
            Bread Loaf 1.00 7.50 7.50
            Fresh Milk 1.00 -2.50 -2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.NeedsReview, result.Status);
        Assert.Contains("cannot share one fiscal document", result.Reason);
    }

    [Fact]
    public void ACorrectionWithoutAReferenceIsHeldAndNamesTheKeyToConfigure()
    {
        var result = new ReceiptParser(Config()).Parse(
            ReturnSlip.Replace("Original Invoice: INV-1001\n", ""));

        Assert.Equal(ParseStatus.NeedsReview, result.Status);
        Assert.Contains("no original invoice number", result.Reason);
        Assert.Contains("parse.corrections.originalInvoiceLabels", result.Reason);
    }

    [Fact]
    public void ACorrectionWithoutAReasonIsSentWithAGenericOneAndWarnedAbout()
    {
        var result = new ReceiptParser(Config()).Parse(
            ReturnSlip.Replace("Return Reason: Wrong size\n", ""));

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Null(result.Receipt!.CreditNoteReason);
        Assert.Contains(result.Warnings, warning => warning.Contains("states no reason"));
    }

    [Fact]
    public void ACreditNoteNeverTakesTheOriginalsNumberAsItsOwn()
    {
        const string text = """
            Store Demo
            RETURN
            Slip 55
            Original Invoice: INV-1001
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Items count: 1
            Total: 2.50
            """;

        // A POS whose own number the parser cannot read: the only invoice-shaped number on the
        // paper is the original's, and filing there would collide with the sale it reverses.
        var config = Config();
        config.ProductStartLine = "Slip";

        var result = new ReceiptParser(config).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Null(result.Receipt!.InvoiceNumber);
        Assert.Equal("CreditNote", result.Receipt.TransactionType);
        Assert.Equal("INV-1001", result.Receipt.RelatedInvoiceNumber);
        Assert.Contains(result.Warnings, warning => warning.Contains("Invoice number not found"));
    }

    [Fact]
    public void AProfileSuppliesTheWordsItsPosPrintsForAReturn()
    {
        const string text = """
            Store Demo
            GOODS TAKEN BACK
            Order No.: TB-2001
            Reversal of: TB-1999
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Items count: 1
            Total: 2.50
            """;

        var config = Config();
        config.Corrections.CreditNoteKeywords = ["GOODS TAKEN BACK"];
        config.Corrections.OriginalInvoiceLabels = ["Reversal of"];

        var receipt = new ReceiptParser(config).Parse(text).Receipt;

        Assert.Equal("CreditNote", receipt!.TransactionType);
        Assert.Equal("TB-1999", receipt.RelatedInvoiceNumber);
    }

    [Fact]
    public void AProfileThatStatesNoCorrectionWordsReliesOnTheAmountAlone()
    {
        var config = Config();
        config.Corrections.CreditNoteKeywords = [];
        config.Corrections.CreditNoteNumberPrefixes = [];

        var announced = new ReceiptParser(config).Parse(ReturnSlip).Receipt;
        Assert.Equal("FiscalInvoice", announced!.TransactionType);

        var signed = Config();
        signed.Corrections.CreditNoteKeywords = [];
        signed.Corrections.CreditNoteNumberPrefixes = [];
        var negativeReceipt = new ReceiptParser(signed).Parse("""
            Store Demo
            RETURN
            Order No.: CN-1001
            Original Invoice: INV-1001
            --------------------------------
            Bread Loaf 1.00 -2.50 -2.50
            --------------------------------
            Items count: 1
            Total: -2.50
            """).Receipt;
        Assert.Equal("CreditNote", negativeReceipt!.TransactionType);
    }

    [Fact]
    public void TurningOffTheNegativeTotalInferenceLeavesTheDocumentASale()
    {
        var config = Config();
        config.Corrections.NegativeTotalMeansCreditNote = false;
        config.Corrections.CreditNoteKeywords = [];
        config.Corrections.CreditNoteNumberPrefixes = [];

        var receipt = new ReceiptParser(config).Parse("""
            Store Demo
            Order No.: INV-1005
            --------------------------------
            Bread Loaf 1.00 -2.50 -2.50
            --------------------------------
            Items count: 1
            Total: -2.50
            """).Receipt;

        Assert.Equal("FiscalInvoice", receipt!.TransactionType);
        Assert.Null(receipt.RelatedInvoiceNumber);
    }

    [Fact]
    public void ADebitNoteAnnouncedAboveTheItemsIsFiledAsADebitNote()
    {
        const string text = """
            Store Demo
            DEBIT NOTE
            Order No.: DN-1006
            Original Invoice: INV-1001
            --------------------------------
            Undercharged Freight 1.00 12.00 12.00
            --------------------------------
            Items count: 1
            Total: 12.00
            """;

        var receipt = new ReceiptParser(Config()).Parse(text).Receipt;

        Assert.Equal("DebitNote", receipt!.TransactionType);
        Assert.Equal("INV-1001", receipt.RelatedInvoiceNumber);
    }

    [Fact]
    public void ASageReturnWithNegativeRowsIsFiledAsACreditNote()
    {
        const string text = """
            CREDIT NOTE
            Memp Hardware & Furniture Fittings
            Number:CN0011687
            Date:22/10/2026
            Original Invoice:INV0011686
            Reason:Goods returned damaged
            Page:
            1/1
            Cash Customer
            DescriptionQuantityExcl. PriceDisc %VAT %Exclusive TotalInclusive Total
            003569 - BORING BIT1M-113.040.00%15.00%M-113.04M-130.00
            BANKING DETAILS:
            Total:M-130.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("CreditNote", receipt.TransactionType);
        Assert.Equal("CN0011687", receipt.InvoiceNumber);
        Assert.Equal("INV0011686", receipt.RelatedInvoiceNumber);
        Assert.Equal("Goods returned damaged", receipt.CreditNoteReason);
        Assert.Equal(-130.00m, receipt.Items[0].Amount);
        Assert.Equal(-130.00m, receipt.Total);
        Assert.Equal(-16.96m, receipt.TaxTotal);
    }

    [Fact]
    public void TheBuiltInCorrectionVocabularyIsTheOneTheShippedProfileCarries()
    {
        var fromConfig = Json(new ParseConfig().ExportRules().Corrections);
        var sage = new ParseProfileStore(Path.Combine(AppContext.BaseDirectory, "profiles"), Path.GetTempPath())
            .Find("sage")!
            .Rules
            .Corrections;

        Assert.Equal(fromConfig, Json(sage));
    }

    [Fact]
    public void AProfileThatOmitsTheSignSwitchLeavesTheInferenceOn()
    {
        var rules = InstalledRules("""
            { "name": "sign-off", "title": "Sign", "rules": { "corrections": { "creditNoteKeywords": ["BACK"] } } }
            """);

        Assert.Equal(new[] { "BACK" }, rules.Corrections!.CreditNoteKeywords);
        Assert.True(rules.Corrections.NegativeTotalMeansCreditNote);
    }

    [Fact]
    public void AProfileThatTurnsTheSignSwitchOffSaysSoOnThePaper()
    {
        var rules = InstalledRules("""
            { "name": "sign-off", "title": "Sign", "rules": { "corrections": { "negativeTotalMeansCreditNote": false } } }
            """);

        Assert.False(rules.Corrections!.NegativeTotalMeansCreditNote);
    }

    [Fact]
    public void AReturnIsSentAsTheBodyTheGatewaySchemaAccepts()
    {
        var parseConfig = Config();
        var receipt = new ReceiptParser(parseConfig).Parse(ReturnSlip).Receipt!;

        var body = JsonDocument.Parse(
            JsonSerializer.Serialize(
                FiscalizeRequestFactory.Create(receipt, parseConfig),
                FiscalStackClient.RequestJsonOptions)).RootElement;

        Assert.Equal("CreditNote", body.GetProperty("transactionType").GetString());
        Assert.Equal("CN-1001", body.GetProperty("invoiceNumber").GetString());
        Assert.Equal("INV-1001", body.GetProperty("relatedInvoiceNumber").GetString());
        Assert.Equal("Wrong size", body.GetProperty("creditNoteReason").GetString());

        // The schema rejects a negative quantity or price: the gateway signs the document
        // itself, so the line amounts travel as positive figures.
        var item = body.GetProperty("items")[0];
        Assert.Equal(1m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(2.50m, item.GetProperty("unitPrice").GetDecimal());

        // An explicit null is a validation error that rejects the whole document.
        Assert.DoesNotContain(body.EnumerateObject(), property =>
            property.Value.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void ASlipWhoseLinesPrintNegativeTravelsAsAPositiveCreditNote()
    {
        const string text = """
            Store Demo
            Order No.: CN-1002
            Original Invoice: INV-1001
            Reason: Faulty
            --------------------------------
            Bread Loaf 1.00 -2.50 -2.50
            --------------------------------
            Items count: 1
            Total: -2.50
            """;

        var parseConfig = Config();
        var receipt = new ReceiptParser(parseConfig).Parse(text).Receipt!;
        var request = FiscalizeRequestFactory.Create(receipt, parseConfig);

        Assert.Equal("CreditNote", request.TransactionType);
        Assert.Equal(-2.50m, receipt.Total);
        Assert.Equal(2.50m, request.Items[0].UnitPrice);
        Assert.Equal("Faulty", request.CreditNoteReason);
    }

    /// <summary>The profile as a till would install it: written to a file, imported, then applied to live rules.</summary>
    private static ParseProfileRules InstalledRules(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "corrections-profile", Guid.NewGuid().ToString("N"));
        var profiles = Path.Combine(root, "profiles");
        Directory.CreateDirectory(profiles);
        var received = Path.Combine(root, "received.json");
        File.WriteAllText(received, json);

        try
        {
            var store = new ParseProfileStore(
                Path.Combine(AppContext.BaseDirectory, "profiles"), profiles);
            store.Import(received);
            var config = new ParseConfig();
            store.Find("sign-off")!.Rules.ApplyTo(config);
            return config.ExportRules();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Json(object? value) =>
        System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
}
