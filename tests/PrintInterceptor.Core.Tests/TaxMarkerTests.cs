using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// A POS states a line's VAT status in three places: a code in or after the amount column,
/// a bracketed note, or a header over a block of items. These tests pin those windows, what
/// is deliberately outside them, and the rule that an unclear line never stops a sale — the
/// bucket the till picks here is the ZIMRA tax ID the gateway files the line under.
/// </summary>
public class TaxMarkerTests
{
    private static ParseConfig Config() => new()
    {
        ProductStartLine = "Order No.",
        ProductEndLine = "Items count",
        InvoiceNumber = "Order No.",
        VatFlag = "1",
        VatRate = 0.15
    };

    [Fact]
    public void ACodeAfterTheAmountExemptsTheLineItIsPrintedOn()
    {
        const string text = """
            Store Demo
            Order No.: INV-1001
            --------------------------------
            Bread Loaf 1.00 2.50 2.50 EX
            Fresh Milk 2.00 1.25 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = result.Receipt!;
        Assert.Equal("EXEMPT", receipt.Items[0].TaxType);
        Assert.Equal(0m, receipt.Items[0].TaxRate);
        Assert.Equal("STANDARD", receipt.Items[1].TaxType);

        // Only the exempt line pays no VAT; the till's own copy shows the rest.
        Assert.Equal(0.33m, receipt.TaxTotal);
    }

    [Fact]
    public void AProductThatMentionsTaxLanguageIsStillTaxable()
    {
        const string text = """
            Store Demo
            Order No.: INV-1002
            --------------------------------
            Zero Sugar Coke 1.00 2.50 2.50
            Exempt Levy Water 1.00 2.50 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.All(result.Receipt!.Items, item => Assert.Equal("STANDARD", item.TaxType));
        Assert.Equal(0.66m, result.Receipt.TaxTotal);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("tax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABracketedNoteExemptsItsLine()
    {
        const string text = """
            Store Demo
            Order No.: INV-1003
            --------------------------------
            Statutory Levy (EXEMPT) 1.00 5.00 5.00
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Items count: 2
            Total: 7.50
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal("EXEMPT", result.Receipt!.Items[0].TaxType);
        Assert.Equal("STANDARD", result.Receipt.Items[1].TaxType);
    }

    [Fact]
    public void AHeaderNamesTheBlockOfItemsBelowIt()
    {
        const string text = """
            Memp Hardware
            Order No.: INV-1004
            --------------------------------
            EXEMPT SUPPLIES
            Bread Loaf 1.00 2.50 2.50
            ZERO RATED
            Fresh Milk 2.00 1.25 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal("EXEMPT", result.Receipt!.Items[0].TaxType);
        Assert.Equal("ZERO_RATED", result.Receipt.Items[1].TaxType);
        Assert.Equal(0m, result.Receipt.TaxTotal);

        // Every line declared its status, so there is nothing to warn about.
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("tax marker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AZeroPercentLineWithoutACodeIsReadAsZeroRatedAndSaysSo()
    {
        const string text = """
            Tax Invoice
            Memp Hardware & Furniture Fittings
            Number:INV0011690
            Date:22/10/2026
            DescriptionQuantityExcl. PriceDisc %VAT %Excl. TotalIncl. Total
            BASIC0001 - BANK CHARGE (
            ESSENTIAL )
            1.00M100.000.00%0.00%M100.00M100.00
            Total VAT:M0.00
            Total:M100.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal("ZERO_RATED", result.Receipt!.Items[0].TaxType);

        // A 0% figure cannot tell a zero-rated supply from an exempt one, and the operator
        // is the only person who knows which the shop sold.
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("ZERO_RATED", StringComparison.Ordinal) &&
            warning.Contains("parse.taxRules", StringComparison.Ordinal));
    }

    [Fact]
    public void ATaxColumnTurnsAZeroPercentLineIntoAnExemptSupply()
    {
        const string text = """
            Tax Invoice
            Memp Hardware & Furniture Fittings
            Number:INV0011691
            Date:22/10/2026
            DescriptionQuantityExcl. PriceDisc %VAT %Excl. TotalIncl. Total
            BASIC0001 - BANK CHARGE
            1.00M100.000.00%0.00%M100.00M100.00 C
            Total VAT:M0.00
            Total:M100.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var item = Assert.Single(result.Receipt!.Items);
        Assert.Equal("EXEMPT", item.TaxType);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("parse.taxRules", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoMarkersOnOneLinePickTheBetterStatementAndNeverStopTheSale()
    {
        const string text = """
            Store Demo
            Order No.: INV-1005
            --------------------------------
            Consulting Fee (EXEMPT) 1.00 5.00 5.00 A
            --------------------------------
            Items count: 1
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal("EXEMPT", result.Receipt!.Items[0].TaxType);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("STANDARD", StringComparison.Ordinal) &&
            warning.Contains("fiscalised as EXEMPT", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLegacyNonTaxSymbolStillExemptsALineFromItsCode()
    {
        const string text = """
            Store Demo
            Order No.: INV-1006
            --------------------------------
            Levy 1.00 5.00 5.00 NT
            --------------------------------
            Items count: 1
            Total: 5.00
            """;

        var exempt = new ReceiptParser(new ParseConfig
        {
            ProductStartLine = "Order No.",
            ProductEndLine = "Items count",
            InvoiceNumber = "Order No.",
            VatFlag = "1",
            VatRate = 0.15,
            NonTaxSymbol = "NT"
        }).Parse(text);

        Assert.Equal("EXEMPT", exempt.Receipt!.Items[0].TaxType);
    }

    [Fact]
    public void TheLegacyNonTaxSymbolNoLongersReadsTheProductName()
    {
        // The symbol used to be looked for anywhere in the item name, which is how an
        // innocent description could move a sale into another ZIMRA bucket.
        const string text = """
            Store Demo
            Order No.: INV-1007
            --------------------------------
            Levy NT 1.00 5.00 5.00
            --------------------------------
            Items count: 1
            Total: 5.00
            """;

        var result = new ReceiptParser(new ParseConfig
        {
            ProductStartLine = "Order No.",
            ProductEndLine = "Items count",
            InvoiceNumber = "Order No.",
            VatFlag = "1",
            VatRate = 0.15,
            NonTaxSymbol = "NT"
        }).Parse(text);

        Assert.Equal("STANDARD", result.Receipt!.Items[0].TaxType);
    }

    [Fact]
    public void TaxRulesComeFromTheProfileThatWasApplied()
    {
        const string text = """
            Store Demo
            Order No.: INV-1008
            --------------------------------
            Bread Loaf 1.00 2.50 2.50 W
            --------------------------------
            Items count: 1
            Total: 2.50
            """;

        Assert.Equal("STANDARD", new ReceiptParser(Config()).Parse(text).Receipt!.Items[0].TaxType);

        var config = Config();
        new ParseProfileRules
        {
            TaxRules = new List<TaxMarkerRule>
            {
                new()
                {
                    TaxType = "exempt",
                    Markers = new List<string> { "W" },
                    Where = new List<string> { "slot" }
                }
            }
        }.ApplyTo(config);
        config.NormalizeTaxRules();

        var result = new ReceiptParser(config).Parse(text);

        Assert.Equal("EXEMPT", result.Receipt!.Items[0].TaxType);
    }

    [Fact]
    public void EveryBucketTheParserChoosesIsOneTheGatewayAccepts()
    {
        const string text = """
            Memp Hardware
            Order No.: INV-1009
            Buyer TIN: 1234567890
            --------------------------------
            EXEMPT SUPPLIES
            Bread Loaf 1.00 2.50 2.50
            ZERO RATED
            Fresh Milk 2.00 1.25 2.50
            Standard Goods 1.00 2.50 2.50 A
            --------------------------------
            Items count: 3
            Total: 7.50
            """;

        var config = Config();
        var result = new ReceiptParser(config).Parse(text);
        var request = FiscalizeRequestFactory.Create(result.Receipt!, config);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal(
            new[] { "EXEMPT", "ZERO_RATED", "STANDARD" },
            result.Receipt!.Items.Select(item => item.TaxType));

        // The gateway maps the bucket straight onto a ZIMRA tax ID, so a line it cannot
        // name is a 400 that rejects the whole sale.
        Assert.All(request.Items, item =>
        {
            Assert.NotNull(item.TaxType);
            Assert.True(item.TaxType is "STANDARD" or "ZERO_RATED" or "EXEMPT", $"unexpected taxType {item.TaxType}");
        });
        Assert.Contains("\"EXEMPT\"", JsonSerializer.Serialize(request), StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileCannotInventATaxTypeTheGatewayWouldReject()
    {
        var config = new ParseConfig
        {
            TaxRules = new List<TaxMarkerRule>
            {
                new() { TaxType = "REDUCED", Markers = new List<string> { "R" } }
            },
            DefaultTaxType = "exempt supplies"
        };

        var errors = config.ValidateRules();

        Assert.Contains(errors, error => error.Contains("parse.taxRules entry 'REDUCED'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("parse.defaultTaxType", StringComparison.Ordinal));
    }

    [Fact]
    public void AWindowTheParserNeverReadsIsRejectedAtImport()
    {
        var config = new ParseConfig
        {
            TaxRules = new List<TaxMarkerRule>
            {
                new()
                {
                    TaxType = "EXEMPT",
                    Markers = new List<string> { "EX" },
                    Where = new List<string> { "productname" }
                }
            }
        };

        var errors = config.ValidateRules();

        Assert.Contains(errors, error =>
            error.Contains("window 'productname'", StringComparison.Ordinal) &&
            error.Contains("slot, annotation or section", StringComparison.Ordinal));
    }

    [Fact]
    public void AMarkerMadeOnlyOfFiguresIsRejectedBecauseTheParserReadsItAsAnAmount()
    {
        var config = new ParseConfig
        {
            TaxRules = new List<TaxMarkerRule>
            {
                new() { TaxType = "ZERO_RATED", Markers = new List<string> { "0%" } }
            },
            NonTaxSymbol = "0"
        };

        var errors = config.ValidateRules();

        Assert.Contains(errors, error => error.Contains("parse.taxRules marker '0%'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("parse.nonTaxSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public void AReceiptWithNoMarkersAtAllStaysQuietAndUsesTheDefault()
    {
        const string text = """
            Store Demo
            Order No.: INV-1010
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            Fresh Milk 2.00 1.25 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        // A POS that prints no per-line statement is normal, not a fault: flooding the
        // operator with it would hide the receipts that really are ambiguous.
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("tax marker", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Receipt!.Items, item => Assert.Equal("STANDARD", item.TaxType));
    }

    [Fact]
    public void ASaleWithNoConfiguredRateIsNotFiledAsStandardSupply()
    {
        const string text = """
            Store Demo
            Order No.: INV-1011
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Items count: 1
            Total: 2.50
            """;

        var result = new ReceiptParser(new ParseConfig
        {
            ProductStartLine = "Order No.",
            ProductEndLine = "Items count",
            InvoiceNumber = "Order No.",
            VatRate = 0
        }).Parse(text);

        Assert.Equal("ZERO_RATED", result.Receipt!.Items[0].TaxType);
        Assert.Equal(0m, result.Receipt.TaxTotal);
    }
}
