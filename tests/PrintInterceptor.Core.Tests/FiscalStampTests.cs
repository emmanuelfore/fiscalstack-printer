using PrintInterceptor.Core.Printing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class FiscalStampTests
{
    [Fact]
    public void HeadingMarksADuplicateCopy()
    {
        Assert.Equal(FiscalStamp.Heading, FiscalStamp.HeadingFor(new ReceiptPrintModel()));

        var reprint = new ReceiptPrintModel { IsReprint = true };
        Assert.Equal(FiscalStamp.ReprintHeading, FiscalStamp.HeadingFor(reprint));
        Assert.NotEqual(FiscalStamp.Heading, FiscalStamp.HeadingFor(reprint));
    }

    [Fact]
    public void StampCarriesTheFiscalFieldsThePosCouldNotPrint()
    {
        var model = new ReceiptPrintModel
        {
            InvoiceNumber = "INV-002",
            DateText = "07/07/2026 14:32",
            DeviceId = "36575",
            DeviceSerialNo = "elimuz-01",
            ReceiptCounter = 7,
            ReceiptGlobalNo = 987,
            FiscalDayNo = 12,
            QrCodeUrl = "https://fiscalstack.co.zw/verify/RCPT123"
        };

        Assert.Equal(
            new[]
            {
                ("Invoice No", "7/987"),
                ("Date", "07/07/2026"),
                ("Customer Reference No", "INV-002"),
                ("Fiscal Device ID", "36575"),
                ("Fiscal Day No", "12"),
                ("Device Serial No", "elimuz-01")
            },
            FiscalStamp.Lines(model).Select(line => (line.Label, line.Value)));
    }

    [Fact]
    public void InvoiceNumberIsTheFiscalReceiptNumber()
    {
        var fromReceiptBlock = new ReceiptPrintModel
        {
            InvoiceNumber = "INV-002",
            ReceiptCounter = 7,
            ReceiptGlobalNo = 987
        };
        Assert.Equal("7/987", FiscalStamp.InvoiceNumber(fromReceiptBlock));

        // FiscalStack also returns the global number as the top-level receipt number
        // and omits the receipt block; either way it is the number ZIMRA quotes.
        var topLevelOnly = new ReceiptPrintModel { InvoiceNumber = "INV-002", ReceiptNumber = 4211 };
        Assert.Equal("4211", FiscalStamp.InvoiceNumber(topLevelOnly));

        var dayCounterOnly = new ReceiptPrintModel { ReceiptCounter = 7, ReceiptNumber = 4211 };
        Assert.Equal("7/4211", FiscalStamp.InvoiceNumber(dayCounterOnly));

        var nothingFiscal = new ReceiptPrintModel { InvoiceNumber = "INV-002" };
        Assert.Equal("INV-002", FiscalStamp.InvoiceNumber(nothingFiscal));
    }

    [Fact]
    public void MissingFieldsAreStampedAsNotAvailableRatherThanBlank()
    {
        var model = new ReceiptPrintModel { InvoiceNumber = "INV-002" };

        var lines = FiscalStamp.Lines(model).ToDictionary(line => line.Label, line => line.Value);

        Assert.Equal("INV-002", lines["Invoice No"]);
        Assert.Equal("N/A", lines["Date"]);
        Assert.Equal("N/A", lines["Fiscal Device ID"]);
        Assert.Equal("N/A", lines["Fiscal Day No"]);
        Assert.Equal("N/A", lines["Device Serial No"]);
    }

    [Fact]
    public void FiscalCodeIsStampedOnlyWhenThereIsNoQrToCarryIt()
    {
        var withQr = new ReceiptPrintModel
        {
            FiscalCode = "RCPT1234567890",
            QrCodeUrl = "https://fiscalstack.co.zw/verify/RCPT1234567890"
        };
        Assert.DoesNotContain(FiscalStamp.Lines(withQr), line => line.Label == "Fiscal Code");

        var withoutQr = new ReceiptPrintModel { FiscalCode = "RCPT1234567890" };
        Assert.Equal(
            "RCPT1234567890",
            FiscalStamp.Lines(withoutQr).Single(line => line.Label == "Fiscal Code").Value);
    }

    [Fact]
    public void StampDatesDropTheTimeButKeepTheDay()
    {
        Assert.Equal("07/07/2026", FiscalStamp.DateOnly("07/07/2026 14:32"));
        Assert.Equal("07/07/2026", FiscalStamp.DateOnly("07/07/2026"));
        Assert.Equal("N/A", FiscalStamp.DateOnly(" "));
        Assert.Equal("N/A", FiscalStamp.DateOnly(null));
    }

    [Fact]
    public void StampValuesUseInvariantNumbers()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var model = new ReceiptPrintModel { ReceiptCounter = 7, ReceiptGlobalNo = 987, FiscalDayNo = 12 };

            var lines = FiscalStamp.Lines(model).ToDictionary(line => line.Label, line => line.Value);

            Assert.Equal("7/987", lines["Invoice No"]);
            Assert.Equal("12", lines["Fiscal Day No"]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void VerificationPayloadsAreOptional()
    {
        Assert.Null(FiscalStamp.QrPayload(new ReceiptPrintModel()));
        Assert.Null(FiscalStamp.VerificationUrl(new ReceiptPrintModel { VerifyUrl = "" }));

        var model = new ReceiptPrintModel
        {
            QrCodeUrl = "https://fiscalstack.co.zw/verify/RCPT123",
            VerifyUrl = " "
        };

        Assert.Equal("https://fiscalstack.co.zw/verify/RCPT123", FiscalStamp.QrPayload(model));
        Assert.Null(FiscalStamp.VerificationUrl(model));
    }
}
