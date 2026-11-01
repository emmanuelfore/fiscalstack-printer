using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// The gateway validates this body with a strict schema, so these tests pin the
/// payload to what that schema accepts: enum spellings, a calendar date, positive
/// line amounts, and optionals omitted instead of sent as null.
/// </summary>
public class FiscalizeRequestFactoryTests
{
    private static ParsedReceipt Receipt(
        string currency = "USD",
        string paymentMethod = "CASH",
        string transactionType = "FiscalInvoice",
        string? vatNumber = "123456789",
        string? tin = "1234567890",
        ParsedItem? item = null) => new()
    {
        Items = new[] { item ?? new ParsedItem("Bread", 1m, 2.50m, 2.50m, "STANDARD", 0.15m) },
        InvoiceNumber = "INV-002",
        Date = new DateTimeOffset(2026, 11, 1, 14, 32, 0, TimeSpan.FromHours(2)),
        Currency = currency,
        PaymentMethod = paymentMethod,
        TransactionType = transactionType,
        Buyer = new ParsedBuyer
        {
            Name = "Acme Ltd",
            VatNumber = vatNumber,
            Tin = tin,
            Email = "accounts@acme.co.zw",
            Phone = "+263771234567",
            Address = "12 Samora Machel Ave, Harare"
        },
        Total = 2.50m
    };

    private static FiscalizeRequest Create(ParsedReceipt receipt, string vatFlag = "1") =>
        FiscalizeRequestFactory.Create(receipt, new ParseConfig { VatFlag = vatFlag });

    [Fact]
    public void PayloadMatchesTheGatewaySchemaFieldForField()
    {
        var json = JsonSerializer.Serialize(Create(Receipt()), FiscalStackClient.RequestJsonOptions);

        Assert.Equal(
            """
            {"items":[{"name":"Bread","quantity":1,"unitPrice":2.50,"taxType":"STANDARD","taxInclusive":true}],"buyer":{"registeredName":"Acme Ltd","vatNumber":"123456789","tin":"1234567890","email":"accounts@acme.co.zw","phone":"\u002B263771234567","street":"12 Samora Machel Ave, Harare"},"invoiceNumber":"INV-002","date":"2026-11-01","currency":"USD","paymentMethod":"CASH","transactionType":"FiscalInvoice"}
            """,
            json);
    }

    [Fact]
    public void DateIsACalendarDateNotATimestamp()
    {
        Assert.Equal("2026-11-01", Create(Receipt()).Date);
    }

    [Fact]
    public void MissingDateIsOmittedSoTheGatewayUsesItsOwnDay()
    {
        var request = Create(new ParsedReceipt
        {
            Items = Receipt().Items,
            InvoiceNumber = "INV-003"
        });

        Assert.Null(request.Date);
        Assert.DoesNotContain("\"date\"", Serialise(request));
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("usd", "USD")]
    [InlineData("ZWL", "ZWG")]
    [InlineData("ZWG", "ZWG")]
    [InlineData("LSL", "LSL")]
    [InlineData("M", "LSL")]
    [InlineData("BWP", null)]
    [InlineData("", null)]
    public void EveryCurrencyTheGatewayAcceptsIsSent(string currency, string? expected)
    {
        Assert.Equal(expected, Create(Receipt(currency: currency)).Currency);
    }

    [Theory]
    [InlineData("cash", "CASH")]
    [InlineData("Mobile", "MOBILE")]
    [InlineData("PayNow", null)]
    public void PaymentMethodsAreMappedToTheGatewayEnum(string payment, string? expected)
    {
        Assert.Equal(expected, Create(Receipt(paymentMethod: payment)).PaymentMethod);
    }

    [Theory]
    [InlineData("FiscalInvoice", "FiscalInvoice")]
    [InlineData("creditnote", "CreditNote")]
    [InlineData("Debit Note", null)]
    public void TransactionTypesUseTheExactSpellingTheGatewayExpects(string type, string? expected)
    {
        Assert.Equal(expected, Create(Receipt(transactionType: type)).TransactionType);
    }

    [Theory]
    [InlineData("STANDARD", "STANDARD")]
    [InlineData("zero_rated", "ZERO_RATED")]
    [InlineData("EXEMPT", "EXEMPT")]
    [InlineData("ZERA", null)]
    public void TaxTypesAreMappedOrOmittedForTheGatewayToDefault(string taxType, string? expected)
    {
        var item = new ParsedItem("Bread", 1m, 2.50m, 2.50m, taxType, 0.15m);

        Assert.Equal(expected, Create(Receipt(item: item)).Items[0].TaxType);
    }

    [Fact]
    public void CorrectionLineAmountsStayPositiveBecauseTheGatewaySignsThem()
    {
        // A credit note printed with negative lines would otherwise be rejected:
        // the schema requires a positive quantity and a price of at least zero, and
        // the gateway applies the sign itself from the transaction type.
        var item = new ParsedItem("Bread", -2m, -2.50m, -5m, "STANDARD", 0.15m);

        var sent = Create(Receipt(transactionType: "CreditNote", item: item), vatFlag: "0").Items[0];

        Assert.Equal(2m, sent.Quantity);
        Assert.Equal(2.50m, sent.UnitPrice);
        Assert.False(sent.TaxInclusive);
    }

    [Fact]
    public void DebitNotesGetAReasonToo()
    {
        var request = Create(Receipt(transactionType: "DebitNote"));

        Assert.Equal("Debit note", request.CreditNoteReason);
    }

    [Fact]
    public void BuyerIsOnlySentWhenTheCustomerHasATin()
    {
        // ZIMRA receives a buyer only against a TIN; a name-only block is a rejection.
        var noTin = Create(Receipt(tin: null));

        Assert.Null(noTin.Buyer);
        Assert.DoesNotContain("\"buyer\"", Serialise(noTin));
    }

    [Theory]
    [InlineData("123456789", "123456789")]
    [InlineData("123 456 789", "123456789")]
    [InlineData("98765", null)]
    [InlineData("1234567890", null)]
    public void VatNumberIsSentOnlyAtTheLengthTheGatewayDemands(string vatNumber, string? expected)
    {
        Assert.Equal(expected, Create(Receipt(vatNumber: vatNumber)).Buyer!.VatNumber);
    }

    [Fact]
    public void UnusableContactDetailsAreDroppedRatherThanSent()
    {
        var receipt = new ParsedReceipt
        {
            Items = Receipt().Items,
            InvoiceNumber = "INV-004",
            Buyer = new ParsedBuyer
            {
                Name = "Acme Ltd",
                Tin = "1234567890",
                Email = "not-an-email",
                Phone = "+263 77 1234 567 890 1234"
            }
        };

        var buyer = Create(receipt).Buyer!;

        Assert.Null(buyer.Email);
        Assert.Null(buyer.Phone);
        Assert.Equal("Acme Ltd", buyer.RegisteredName);
        Assert.Equal("1234567890", buyer.Tin);
    }

    [Fact]
    public void BuyerAddressGoesToTheFieldTheGatewayReads()
    {
        Assert.Equal("12 Samora Machel Ave, Harare", Create(Receipt()).Buyer!.Street);
    }

    [Fact]
    public void BlankFieldsAreAbsentFromTheWire()
    {
        // The schema's optionals accept a missing key, not null, so a null would
        // reject the whole sale.
        var request = Create(new ParsedReceipt
        {
            Items = Receipt().Items,
            Currency = "BWP",
            PaymentMethod = "PAYNOW",
            TransactionType = "Quotation",
            Buyer = new ParsedBuyer { Name = "Acme Ltd" }
        });

        var json = Serialise(request);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.Equal(
            """
            {"items":[{"name":"Bread","quantity":1,"unitPrice":2.50,"taxType":"STANDARD","taxInclusive":true}]}
            """,
            json);
    }

    private static string Serialise(FiscalizeRequest request) =>
        JsonSerializer.Serialize(request, FiscalStackClient.RequestJsonOptions);
}
