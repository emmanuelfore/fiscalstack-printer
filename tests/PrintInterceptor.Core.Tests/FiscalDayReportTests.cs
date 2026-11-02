using System.Globalization;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.Reporting;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// The day report is what a shopkeeper signs and an accountant files, so the tests hold it to the
/// documents: it totals what was printed, keeps a refund out of the sales, never invents a tax
/// split for a receipt it can no longer read, and says out loud what is still unresolved.
/// </summary>
public sealed class FiscalDayReportTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 2);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pi-day-" + Guid.NewGuid().ToString("N"));

    private readonly JobLedger _ledger;
    private readonly PrintSnapshotStore _snapshots;
    private readonly FiscalDayReportService _service;

    public FiscalDayReportTests()
    {
        var config = new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig { Name = "Configured Ltd", Tin = "9999999999" }
        };

        _ledger = new JobLedger(Path.Combine(_directory, "state"), NullLog.Instance);
        _snapshots = new PrintSnapshotStore(config, NullLog.Instance);
        _service = new FiscalDayReportService(config, _ledger, _snapshots, NullLog.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void TotalsTheDayByTaxBucketFromTheDocumentsThemselves()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);
        Issue(
            "hash-2", "INV-2", 50m, 0m, receipt: 2, counter: 2,
            lines: new[] { new ReceiptPrintLine("Banking Fee", 1m, 50m, 50m, null, "EXEMPT") },
            taxes: new[] { new ReceiptPrintTax(null, true, 50m, 0m, 50m) });

        var report = _service.Build(Day);

        Assert.Equal(2, report.Sales.Documents);
        Assert.Equal(165m, report.Gross);
        Assert.Equal(15m, report.Tax);
        Assert.Equal(150m, report.Sales.Net);

        Assert.Equal(
            new[] { "15%", "EXEMPT" },
            report.Sales.Buckets.Select(bucket => bucket.Label));
        Assert.Equal(115m, report.Sales.Buckets[0].Gross);
        Assert.Equal(100m, report.Sales.Buckets[0].Net);
        Assert.Equal(1, report.Sales.Buckets[1].Documents);

        Assert.Equal(5, report.FiscalDayNo);
        Assert.Equal("SN-001", report.DeviceSerialNo);
        Assert.Equal("Test Traders", report.CompanyName);
        Assert.Equal(new[] { "USD" }, report.Currencies);
    }

    [Fact]
    public void ReportsOneDateWithoutTouchingAnother()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);
        Issue("hash-2", "INV-2", 230m, 30m, date: Day.AddDays(1), receipt: 2, counter: 2);

        var report = _service.Build(Day);

        Assert.Equal(1, report.Documents);
        Assert.Equal(115m, report.Gross);
        Assert.Equal("2026-11-02", report.BusinessDate);
    }

    [Fact]
    public void ARefundIsKeptApartFromTheSalesAndNetsAgainstThem()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);
        Issue(
            "hash-2", "CN-1", -115m, -15m,
            type: "CreditNote", receipt: 2, counter: 2,
            lines: new[] { new ReceiptPrintLine("Bread Loaf", -1m, -115m, 115m, 15m, "STANDARD") });

        var report = _service.Build(Day);

        Assert.Equal(1, report.Sales.Documents);
        Assert.Equal(1, report.Credits.Documents);
        Assert.Equal(115m, report.Sales.Gross);
        Assert.Equal(-115m, report.Credits.Gross);
        Assert.Equal(0m, report.Gross);
        Assert.Equal(0m, report.Tax);

        Assert.Contains("CREDIT NOTES", report.Render());
        Assert.DoesNotContain("DEBIT NOTES", report.Render());
    }

    [Fact]
    public void ADocumentWithNoPrintedCopyIsCountedButItsTaxIsNeverInvented()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1, saveDocument: false);

        var report = _service.Build(Day);

        Assert.Equal(1, report.Documents);
        Assert.Equal(0, report.Sales.WithItemDetail);
        Assert.Equal(0m, report.Gross);
        Assert.Equal(115m, report.Sales.GrossWithoutItemDetail);
        Assert.Equal(new[] { "INV-1 115.00 USD" }, report.WithoutItemDetail);
        Assert.Empty(report.Lines);

        Assert.Contains("have no printed copy left on this till", report.Render());
    }

    [Fact]
    public void TaxRowsHandedBackAsMagnitudesAreSignedLikeTheDocumentTheyBelongTo()
    {
        // A credit note whose gateway rows came back positive must not add VAT to the day.
        Issue(
            "hash-1", "CN-1", -115m, -15m,
            type: "CreditNote", receipt: 1, counter: 1,
            taxes: new[] { new ReceiptPrintTax(15m, false, 100m, 15m, 115m) });

        var report = _service.Build(Day);

        Assert.Equal(-115m, report.Credits.Gross);
        Assert.Equal(-100m, report.Credits.Net);
        Assert.Equal(-15m, report.Credits.Tax);
        Assert.Equal(-115m, Assert.Single(report.Credits.Buckets).Gross);
    }

    [Fact]
    public void AReceiptWithNoTaxRowsStillTookMoney()
    {
        Issue(
            "hash-1", "INV-1", 50m, 0m, receipt: 1, counter: 1,
            lines: new[] { new ReceiptPrintLine("Cash Sale", 1m, 50m, 50m, null, null) },
            taxes: Array.Empty<ReceiptPrintTax>());

        var report = _service.Build(Day);

        Assert.Equal(50m, report.Gross);
        Assert.Equal(0m, report.Tax);
        Assert.Equal("NO TAX ROWS", report.Sales.Buckets[0].Label);
    }

    [Fact]
    public void AnUnresolvedReceiptIsReportedAsTheDayNotBeingClosedYet()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);

        _ledger.Claim("hash-2", "INV-2", Key(Day), null, 230m, "USD", "FiscalInvoice");
        _ledger.MarkFailure("hash-2", LedgerStatus.AmbiguousFailure, "The request timed out after 30s.");

        var report = _service.Build(Day);

        Assert.Equal(1, report.Documents);
        Assert.Equal(1, report.UnknownOutcome);
        Assert.Equal(115m, report.Gross);

        var text = report.Render();
        Assert.Contains("Outcome unknown", text);
        Assert.Contains("Reconcile unknown receipts now", text);
    }

    [Fact]
    public void AReceiptSignedOnTheTillIsStillADocumentOfTheDay()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1, offline: true);

        var report = _service.Build(Day);

        Assert.Equal(1, report.Documents);
        Assert.Equal(1, report.WaitingForSync);
        Assert.Equal(115m, report.Gross);
        Assert.Contains("waiting to sync", report.Render());

        Issue("hash-2", "INV-2", 115m, 15m, receipt: 2, counter: 2, offline: true, synced: true);
        Assert.Equal(1, _service.Build(Day).WaitingForSync);
    }

    [Fact]
    public void MoreThanOneCurrencyIsAnnouncedRatherThanAddedQuietly()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);
        Issue("hash-2", "INV-2", 200m, 0m, currency: "ZAR", receipt: 2, counter: 2);

        var report = _service.Build(Day);

        Assert.Equal(new[] { "USD", "ZAR" }, report.Currencies);
        Assert.Contains("more than one currency", report.Render());
    }

    [Fact]
    public void AnEmptyDaySaysSoRatherThanPrintingZeroes()
    {
        Issue("hash-1", "INV-1", 115m, 15m, date: Day.AddDays(-1), receipt: 1, counter: 1);

        var report = _service.Build(Day);

        Assert.Equal(0, report.Documents);
        Assert.Contains("No fiscal document was issued for this date", report.Render());
    }

    [Fact]
    public void ExportsOneCsvRowPerPrintedLineCarryingItsFiscalProof()
    {
        Issue(
            "hash-1", "INV-1", 115m, 15m, receipt: 77, counter: 3,
            lines: new[]
            {
                new ReceiptPrintLine("Bread Loaf", 2m, 100m, 50m, 15m, "STANDARD"),
                new ReceiptPrintLine("Banking Fee", 1m, 15m, 15m, null, "EXEMPT")
            });

        var csv = _service.Build(Day).Csv();
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToList();

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("businessDate,invoiceNumber,transactionType,receiptNumber", rows[0]);

        var header = rows[0].Split(',');
        var bread = rows[1].Split(',');
        var fee = rows[2].Split(',');

        Assert.Equal(header.Length, bread.Length);
        Assert.Equal("2026-11-02", bread[Array.IndexOf(header, "businessDate")]);
        Assert.Equal("INV-1", bread[Array.IndexOf(header, "invoiceNumber")]);
        Assert.Equal("77", bread[Array.IndexOf(header, "receiptNumber")]);
        Assert.Equal("FC-INV-1", bread[Array.IndexOf(header, "fiscalCode")]);
        Assert.Equal("Bread Loaf", bread[Array.IndexOf(header, "line")]);
        Assert.Equal("100.00", bread[Array.IndexOf(header, "lineTotal")]);
        Assert.Equal("STANDARD", bread[Array.IndexOf(header, "taxType")]);
        Assert.Equal("15.00", bread[Array.IndexOf(header, "taxRatePercent")]);
        Assert.Equal("EXEMPT", fee[Array.IndexOf(header, "taxType")]);
        Assert.Equal("", fee[Array.IndexOf(header, "taxRatePercent")]);
    }

    [Fact]
    public void AProductNameThatIsASpreadsheetFormulaIsExportedAsText()
    {
        Issue(
            "hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1,
            lines: new[] { new ReceiptPrintLine("=HYPERLINK(\"http://evil\")", 1m, 115m, 115m, 15m, "STANDARD") });

        var csv = _service.Build(Day).Csv();

        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", csv);
    }

    [Fact]
    public void ReadsBackADocumentWhoseReprintSnapshotWasPrunedButWhoseArchiveRemains()
    {
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);

        File.Delete(Path.Combine(_snapshots.DirectoryPath, "hash-1.json"));
        Assert.False(_snapshots.Exists("hash-1"));

        var report = _service.Build(Day);

        Assert.Equal(1, report.Sales.WithItemDetail);
        Assert.Equal(115m, report.Gross);
        Assert.Single(report.Lines);
    }

    [Fact]
    public void TheJobsOfTheDayAreGatheredInFiscalOrderNotInTheOrderTheyWerePrinted()
    {
        // A reprint of yesterday's slip arrives last but belongs in the middle of the sequence.
        Issue("hash-2", "INV-2", 230m, 30m, receipt: 2, counter: 2);
        Issue("hash-1", "INV-1", 115m, 15m, receipt: 1, counter: 1);

        var entries = _ledger.ForBusinessDate(Key(Day));

        Assert.Equal(new[] { "INV-1", "INV-2" }, entries.Select(entry => entry.InvoiceNumber));
        Assert.Empty(_ledger.ForBusinessDate(Key(Day.AddDays(1))));
    }

    private static string Key(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A document the till issued and printed: the ledger entry the gateway answered, plus the
    /// print model that went to the printer. Defaults are one standard-rated line so a test only
    /// states what it is about.
    /// </summary>
    private void Issue(
        string hash,
        string invoice,
        decimal total,
        decimal tax,
        string type = "FiscalInvoice",
        DateOnly? date = null,
        string currency = "USD",
        long? receipt = null,
        int? counter = null,
        int? fiscalDay = 5,
        string? fiscalCode = null,
        ReceiptPrintLine[]? lines = null,
        ReceiptPrintTax[]? taxes = null,
        bool saveDocument = true,
        bool offline = false,
        bool synced = false)
    {
        var businessDate = date ?? Day;
        _ledger.Claim(hash, invoice, Key(businessDate), null, total, currency, type);

        var code = fiscalCode ?? $"FC-{invoice}";
        var response = new FiscalizeResponse
        {
            Success = true,
            FiscalCode = code,
            ReceiptNumber = receipt,
            Receipt = new FiscalReceipt
            {
                InvoiceNo = invoice,
                ReceiptType = type,
                ReceiptCurrency = currency,
                ReceiptTotal = total,
                ReceiptCounter = counter,
                ReceiptGlobalNo = counter,
                FiscalDayNo = fiscalDay
            }
        };

        if (!offline)
        {
            _ledger.MarkFiscalised(hash, response);
        }
        else
        {
            _ledger.MarkFiscalisedOffline(hash, new OfflineSignedReceipt
            {
                DeviceId = "812",
                ReceiptType = type,
                Currency = currency,
                ReceiptDate = businessDate.ToDateTime(TimeOnly.MinValue).ToString("o", CultureInfo.InvariantCulture),
                ReceiptCounter = counter ?? 0,
                ReceiptGlobalNo = (int)(receipt ?? 0),
                FiscalDayNo = fiscalDay ?? 0,
                ReceiptTotal = total,
                TaxTotal = tax,
                Hash = code,
                Signature = "sig",
                VerificationCode = "VC",
                QrCodeUrl = "https://verify.test/VC"
            });

            if (synced)
            {
                _ledger.MarkSynced(hash, response);
            }
        }

        if (!saveDocument)
        {
            return;
        }

        _snapshots.Save(
            hash,
            new ReceiptPrintModel
            {
                CompanyName = "Test Traders",
                CompanyTin = "1234567890",
                CompanyVatNumber = "4008000",
                InvoiceNumber = invoice,
                DateText = businessDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " 10:15",
                Currency = currency,
                TransactionType = type,
                Total = total,
                TaxTotal = tax,
                Subtotal = total - tax,
                FiscalCode = code,
                DeviceId = "812",
                DeviceSerialNo = "SN-001",
                ReceiptNumber = receipt,
                ReceiptCounter = counter,
                ReceiptGlobalNo = counter,
                FiscalDayNo = fiscalDay,
                IsOffline = offline,
                Items = lines ?? new[]
                {
                    new ReceiptPrintLine("Bread Loaf", 1m, total, total, Percent(tax, total), "STANDARD")
                },
                Taxes = taxes ?? new[]
                {
                    new ReceiptPrintTax(Percent(tax, total), tax == 0m, total - tax, tax, total)
                }
            });
    }

    /// <summary>The rate a document's VAT implies, so a test can state money rather than percentages.</summary>
    private static decimal? Percent(decimal tax, decimal gross)
    {
        if (tax == 0m)
        {
            return null;
        }

        var net = gross - tax;
        return net == 0m ? null : Math.Round(tax / net * 100m, 2, MidpointRounding.AwayFromZero);
    }
}
