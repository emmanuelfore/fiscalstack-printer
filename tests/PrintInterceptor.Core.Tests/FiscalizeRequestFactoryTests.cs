using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class FiscalizeRequestFactoryTests
{
    private static ParsedReceipt Receipt(string vatNumber = "12345", string tin = "1234567890") => new()
    {
        Items = new[] { new ParsedItem("Bread", 1m, 2.50m, 2.50m, "STANDARD", 0.15m) },
        InvoiceNumber = "INV-1",
        Currency = "USD",
        PaymentMethod = "CASH",
        Buyer = new ParsedBuyer
        {
            Name = "Acme Ltd",
            VatNumber = vatNumber,
            Tin = tin
        },
        Total = 2.50m
    };

    [Fact]
    public void BuyerIdentifiersArePassedThroughWithoutLocalValidation()
    {
        var request = FiscalizeRequestFactory.Create(Receipt(), new ParseConfig { VatFlag = "1" });

        Assert.NotNull(request.Buyer);
        Assert.Equal("12345", request.Buyer!.VatNumber);
        Assert.Equal("1234567890", request.Buyer.Tin);
        Assert.Equal("Acme Ltd", request.Buyer.RegisteredName);
    }

    [Fact]
    public void NonZimraIdentifierLengthsAreKept()
    {
        var request = FiscalizeRequestFactory.Create(
            Receipt(vatNumber: "12345678", tin: "12345678901"),
            new ParseConfig { VatFlag = "1" });

        Assert.Equal("12345678", request.Buyer!.VatNumber);
        Assert.Equal("12345678901", request.Buyer.Tin);
    }

    [Fact]
    public void TaxInclusiveFollowsVatFlag()
    {
        var inclusive = FiscalizeRequestFactory.Create(Receipt(), new ParseConfig { VatFlag = "1" });
        var exclusive = FiscalizeRequestFactory.Create(Receipt(), new ParseConfig { VatFlag = "0" });

        Assert.True(inclusive.Items[0].TaxInclusive);
        Assert.False(exclusive.Items[0].TaxInclusive);
    }
}
