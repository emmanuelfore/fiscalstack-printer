using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class ReceiptParserTests
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

    private const string Sample = """
        Store Demo
        USD Cash Sale
        Order No.: INV-1001
        Buyer Name: Acme Ltd
        Buyer TIN: 1234567890
        Buyer VAT: 123456789
        Date: 29/10/2026 10:15
        --------------------------------
        Bread Loaf 1.00 2.50 2.50
        Fresh Milk 2.00 1.25 2.50
        --------------------------------
        Items count: 2
        Total: 5.00
        Payment: CASH
        """;

    [Fact]
    public void ParsesItemsTotalsAndMetadata()
    {
        var result = new ReceiptParser(Config()).Parse(Sample);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);

        Assert.Equal("INV-1001", receipt.InvoiceNumber);
        Assert.Equal(2, receipt.Items.Count);
        Assert.Equal("Bread Loaf", receipt.Items[0].Name);
        Assert.Equal(1m, receipt.Items[0].Quantity);
        Assert.Equal(2.50m, receipt.Items[0].UnitPrice);
        Assert.Equal(2.50m, receipt.Items[0].Amount);
        Assert.Equal(5.00m, receipt.Total);
        Assert.Equal(0.66m, receipt.TaxTotal);
        Assert.Equal("USD", receipt.Currency);
        Assert.Equal("CASH", receipt.PaymentMethod);
        Assert.Equal("Acme Ltd", receipt.Buyer.Name);
        Assert.Equal("1234567890", receipt.Buyer.Tin);
        Assert.Equal(new DateTime(2026, 10, 29, 10, 15, 0), receipt.Date!.Value.DateTime);
    }

    [Fact]
    public void ParsesTwoLineFiscalRegisterItems()
    {
        const string text = """
            Store Demo
            FS No.00000259
            21/07/2025 11:46
            34400 x 2.400
            BIRTHDAY-DICORAN *82,560.00
            100 x 3
            PEPSI 300.00
            TOTAL *82,860.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal(2, receipt.Items.Count);
        Assert.Equal("BIRTHDAY-DICORAN", receipt.Items[0].Name);
        Assert.Equal(2.4m, receipt.Items[0].Quantity);
        Assert.Equal(34400m, receipt.Items[0].UnitPrice);
        Assert.Equal(82560m, receipt.Items[0].Amount);
        Assert.Equal("PEPSI", receipt.Items[1].Name);
        Assert.Equal(3m, receipt.Items[1].Quantity);
        Assert.Equal(300m, receipt.Items[1].Amount);
        Assert.Equal(82860m, receipt.Total);
    }

    [Fact]
    public void TwoLineItemsThatDoNotBalanceAreHeldForReview()
    {
        const string text = """
            Store Demo
            34400 x 2.400
            BIRTHDAY-DICORAN *80,000.00
            TOTAL *80,000.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.NeedsReview, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("Two-line"));
    }

    [Fact]
    public void NonZimraBuyerIdentifierLengthsAreNotFlagged()
    {
        const string text = """
            Store Demo
            Order No.: INV-3003
            Buyer Name: Thabo Stores
            Buyer TIN: 12345678901
            Buyer VAT: 12345678
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Items count: 1
            Total: 2.50
            Payment: CASH
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("TIN") || w.Contains("VAT number"));
        Assert.Equal("12345678901", result.Receipt!.Buyer.Tin);
        Assert.Equal("12345678", result.Receipt.Buyer.VatNumber);
    }

    [Fact]
    public void ZeroItemsIsHeldForReview()
    {
        const string text = """
            Store Demo
            Order No.: INV-2002
            Items count: 0
            Total: 0.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.NeedsReview, result.Status);
        Assert.Contains("No line items", result.Reason);
    }

    [Fact]
    public void PrintedTotalMismatchIsHeldForReview()
    {
        var text = Sample.Replace("Total: 5.00", "Total: 4.00");

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.NeedsReview, result.Status);
        Assert.Contains("does not match the printed total", result.Reason);
    }

    [Fact]
    public void ParsesSageTaxInvoicePdfLayout()
    {
        const string text = """
            Tax Invoice
            Memp Hardware & Furniture Fittings
            Number:INV0011686
            Date:22/10/2026
            Page:
            1/1
            Reference:
            Sales Rep:Setumo Mphana
            VAT No:50024074
            Along Main South Road 1Along Main South Road 1
            BorokhoanengBorokhoaneng
            Maseru 100Maseru 100
            LesothoLesothoDue Date31/10/2026
            Overall Discount%:0.00%
            Online quotations: mathulo@mempholdings.co.ls+266 5909 0209
            Cash Customer
            Customer VAT No:
            DescriptionQuantityExcl. PriceDisc %VAT %Exclusive TotalInclusive Total
            RPREM19OAK20 - PREPACK DOUBLE
            BRACKET WITH LID OAK PER 20
            3M15.650.00%15.00%M46.95M53.99
            003569 - BORING BIT1M113.040.00%15.00%M113.04M130.00
            BANKING DETAILS:
            Bank: Standard Lesotho Bank
            Account Name: Memp Hardware
            Account Number: 9080010336291
            Total Discount:M0.00
            Total Exclusive:M159.99
            Total VAT:M24.00
            Sub Total:M183.99
            Total:M183.99
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("INV0011686", receipt.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 10, 22), receipt.Date!.Value.Date);
        Assert.Equal("LSL", receipt.Currency);
        Assert.Equal("CASH", receipt.PaymentMethod);
        Assert.Equal("Cash Customer", receipt.Buyer.Name);
        Assert.Null(receipt.Buyer.VatNumber);
        Assert.Equal(2, receipt.Items.Count);
        Assert.Equal("RPREM19OAK20 - PREPACK DOUBLE BRACKET WITH LID OAK PER 20", receipt.Items[0].Name);
        Assert.Equal(3m, receipt.Items[0].Quantity);
        Assert.Equal(53.99m, receipt.Items[0].Amount);
        Assert.Equal(0.15m, receipt.Items[0].TaxRate);
        Assert.Equal("003569 - BORING BIT", receipt.Items[1].Name);
        Assert.Equal(130.00m, receipt.Items[1].Amount);
        Assert.Equal(183.99m, receipt.Total);
        Assert.Equal(24.00m, receipt.TaxTotal);
    }

    [Fact]
    public void ParsesZohoBooksTaxInvoicePdfLayout()
    {
        const string text = """
            COPY TAX INVOICE
            NUMBER:INV0010564
            REFERENCE:
            DATE:21/10/2026
            DUE DATE:
            31/10/2026
            SALES REP:
            OVERALL DISCOUNT %:0.00%
            PAGE:
            1/1
            FROMTO
            MEMP HOLDINGS PTY LTDMR MOLEFI TUTUBALA
            VAT NO:50024074CUSTOMER VAT NO:
            PHYSICAL ADDRESS:
            POSTAL ADDRESS:
            POSTAL ADDRESS:PHYSICAL ADDRESS:
            P O Box 12256Along Main South Road 1
            Maseru 100Borokhoaneng
            Lesotho
            Maseru 100
            +266 53659169
            DescriptionQuantityExcl. PriceDisc %VAT %Excl. TotalIncl. Total
            NOVA0070 - WHITE MELAMINE TEXT (
            ESSENTIAL )
            1.00M643.480.00%15.00%M643.48M740.00
            BANKING DETAILS:
            Account name: Memp Holdings
            Account type: Business Current Account
            Account Number: 9080005627148
            Sort Code: 060667
            Bring proof of deposit or transfer of funds.
            Ecocash - Code: 48869 (Memp Suppliers)
            Mpesa - Till No: 1219 (Memp Suppliers)
            Total Discount:M0.00
            Total Exclusive:M643.48
            Total VAT:
            M96.52
            Sub Total:
            M740.00
            Total Rounding:M0.00
            Grand Total:M740.00
            BALANCE DUE
            M0.00
            """;

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        var receipt = Assert.IsType<ParsedReceipt>(result.Receipt);
        Assert.Equal("INV0010564", receipt.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 10, 21), receipt.Date!.Value.Date);
        Assert.Equal("LSL", receipt.Currency);
        Assert.Equal("TRANSFER", receipt.PaymentMethod);
        Assert.Single(receipt.Items);
        Assert.Equal("NOVA0070 - WHITE MELAMINE TEXT ( ESSENTIAL )", receipt.Items[0].Name);
        Assert.Equal(1m, receipt.Items[0].Quantity);
        Assert.Equal(740.00m, receipt.Items[0].Amount);
        Assert.Equal(740.00m, receipt.Total);
        Assert.Equal(96.52m, receipt.TaxTotal);
    }

    [Fact]
    public void MissingInvoiceNumberStillParsesWithWarning()
    {
        var text = Sample
            .Replace("Order No.: INV-1001", "Store Ref 42")
            .Replace("Items count: 2", "Items count: 2");

        var result = new ReceiptParser(Config()).Parse(text);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Null(result.Receipt!.InvoiceNumber);
        Assert.Contains(result.Warnings, warning => warning.Contains("Invoice number not found"));
    }
}
