using System.Text;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// One document must stay one sale. A till that sends the same slip twice - a customer copy and a
/// file copy, an operator pressing Print again because nothing came out yet, a POS that re-renders
/// its own invoice - is not making a second sale, and the bytes it prints are not a second
/// document either. These tests pin what the interceptor does with a copy it has already seen:
/// reprint the proof that exists, retry on the record that failed, hold the slip that cannot be
/// filed, and never open a second ledger entry for one invoice.
/// </summary>
public sealed class DuplicatePrintTests : IDisposable
{
    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "pi-duplicate-" + Guid.NewGuid().ToString("N"));

    private readonly List<StubHttpServer> _servers = new();

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        try
        {
            if (Directory.Exists(_data))
            {
                Directory.Delete(_data, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task ASecondCopyOfAFiscalisedInvoiceIsReprintedAndNeverSentAgain()
    {
        var rig = Harness(FiscalisedEveryTime);

        await rig.SendAsync(Sale());
        await rig.SendAsync(Sale());

        // The proof already exists, so the second copy costs no request and no waiting on ZIMRA.
        Assert.Single(rig.Posts);
        Assert.Equal(2, rig.Printer.Models.Count);
        Assert.False(rig.Printer.Models[0].IsReprint);
        Assert.True(rig.Printer.Models[1].IsReprint);
        Assert.Equal("FC-77", rig.Printer.Models[1].FiscalCode);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.Fiscalised, entry.Status);
        Assert.Equal(1, entry.DuplicateAttemptCount);
        Assert.Equal(1, entry.ReprintCount);
    }

    [Fact]
    public async Task ACopyThatRePrintedWithDifferentSpacingIsStillTheOneDocument()
    {
        // The hash cannot be the identity of a sale: a driver that reflows the slip changes the
        // bytes while the invoice stays the one the accountant already issued a receipt for.
        var rig = Harness(FiscalisedEveryTime);

        await rig.SendAsync(Sale());
        await rig.SendAsync(Sale(footer: "Thank you for your custom."));

        Assert.Single(rig.Posts);
        Assert.Equal(2, rig.Printer.Models.Count);
        Assert.True(rig.Printer.Models[1].IsReprint);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal("INV-1001", entry.InvoiceNumber);
        Assert.Equal(1, entry.DuplicateAttemptCount);
    }

    [Fact]
    public async Task AnInvoiceNumberReRenderedWithSpacesIsStillTheOneDocument()
    {
        // The second print writes the same number with different spacing. The accountant's
        // identity is the letters and digits, so this must not become a second sale.
        var rig = Harness(FiscalisedEveryTime);

        await rig.SendAsync(Sale(invoiceNumber: "INV-1001"));
        await rig.SendAsync(Sale(invoiceNumber: "INV 1001"));

        Assert.Single(rig.Posts);
        Assert.Equal(2, rig.Printer.Models.Count);
        Assert.True(rig.Printer.Models[1].IsReprint);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(1, entry.DuplicateAttemptCount);
    }

    [Fact]
    public async Task ASecondCopyOfAnUnsettledJobIsNotedAndNothingIsSentOrPrinted()
    {
        // A 5xx leaves the receipt possibly already in ZIMRA. Pressing Print again must not add a
        // second one; reconciliation is what settles it, so the copy is only counted.
        var rig = Harness(_ => StubHttpServer.Json(500, """{"message":"Failed to fiscalise invoice"}"""));

        await rig.SendAsync(Sale());
        await rig.SendAsync(Sale());

        Assert.Single(rig.Posts);
        Assert.Empty(rig.Printer.Models);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.AmbiguousFailure, entry.Status);
        Assert.Equal(1, entry.DuplicateAttemptCount);
    }

    [Fact]
    public async Task ARejectedInvoiceIsSentAgainAsAnotherAttemptOnTheOneRecord()
    {
        // The first attempt never produced a receipt, so the copy is a retry rather than a second
        // sale: it goes back to the gateway, but it is written onto the record that already exists.
        var attempts = 0;
        var rig = Harness(target => target.Contains("/fiscalize") && ++attempts == 1
            ? StubHttpServer.Json(400, """
                {"success":false,"error":"VALIDATION_ERROR: Invalid request body [currency: Invalid enum value.]"}
                """)
            : FiscalisedEveryTime(target));

        await rig.SendAsync(Sale());
        Assert.Equal(LedgerStatus.Rejected, Assert.Single(rig.Ledger.All()).Status);

        await rig.SendAsync(Sale());

        Assert.Equal(2, rig.Posts.Count);
        Assert.Single(rig.Printer.Models);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.Fiscalised, entry.Status);
        Assert.Equal(1, entry.DuplicateAttemptCount);
        Assert.Equal("FC-77", entry.FiscalCode);
    }

    [Fact]
    public async Task ARetryOfARejectedSlipThatRePrintsDifferentlyIsStillOneRecord()
    {
        var attempts = 0;
        var rig = Harness(target => target.Contains("/fiscalize") && ++attempts == 1
            ? StubHttpServer.Json(400, """
                {"success":false,"error":"The invoice number is already fiscalised."}
                """)
            : FiscalisedEveryTime(target));

        await rig.SendAsync(Sale());
        await rig.SendAsync(Sale(footer: "Copy for the file."));

        Assert.Equal(2, rig.Posts.Count);
        Assert.Equal(2, rig.Posts.Count(post => post.Body.Contains("INV-1001", StringComparison.Ordinal)));

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.Fiscalised, entry.Status);
        Assert.Equal(1, entry.DuplicateAttemptCount);
    }

    [Fact]
    public async Task ASlipDatedBeforeTodayIsSentWithItsPrintedDate()
    {
        // The gateway decides which fiscal day it accepts. The till sends the date the POS
        // printed instead of holding the sale locally on an operator-side calendar rule.
        var rig = Harness(FiscalisedEveryTime);
        var printedDate = DateTimeOffset.Now.AddDays(-7);
        var lastWeek = printedDate.ToString("dd/MM/yyyy");

        await rig.SendAsync(Sale(on: lastWeek));

        var post = Assert.Single(rig.Posts);
        Assert.Contains(printedDate.ToString("yyyy-MM-dd"), post.Body, StringComparison.Ordinal);
        Assert.Single(rig.Printer.Models);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.Fiscalised, entry.Status);
        Assert.Equal(lastWeek, DateTime.ParseExact(entry.BusinessDate, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture).ToString("dd/MM/yyyy"));
    }

    [Fact]
    public async Task ARepeatOfAnOlderSlipStaysOnTheOneRecord()
    {
        var rig = Harness(FiscalisedEveryTime);
        var lastWeek = DateTimeOffset.Now.AddDays(-7).ToString("dd/MM/yyyy");

        await rig.SendAsync(Sale(on: lastWeek));
        await rig.SendAsync(Sale(on: lastWeek, footer: "Duplicate copy."));

        Assert.Single(rig.Posts);
        Assert.Equal(2, rig.Printer.Models.Count);
        Assert.True(rig.Printer.Models[1].IsReprint);

        var entry = Assert.Single(rig.Ledger.All());
        Assert.Equal(LedgerStatus.Fiscalised, entry.Status);
        Assert.Equal(1, entry.DuplicateAttemptCount);
    }

    [Fact]
    public async Task TwoDifferentInvoicesOnTheSameDayAreTwoRecords()
    {
        // The duplicate checks key on the number the till printed, so they must not merge the day's
        // separate sales into one document.
        var rig = Harness(FiscalisedEveryTime);

        await rig.SendAsync(Sale(invoiceNumber: "INV-2001"));
        await rig.SendAsync(Sale(invoiceNumber: "INV-2002"));

        Assert.Equal(2, rig.Posts.Count);
        Assert.Equal(2, rig.Ledger.All().Count);
    }

    private Rig Harness(Func<string, StubResponse> respond)
    {
        var server = new StubHttpServer(respond);
        _servers.Add(server);

        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{server.Port}/api/v1/",
            ApiKey = "sk_test_example",
            Storage = new StorageConfig { DataDirectory = _data, RetainJobs = true },
            Printing = new PrintingConfig { Enabled = true, RetryCount = 0 }
        };

        return new Rig(config, server);
    }

    private static StubResponse FiscalisedEveryTime(string target) =>
        target.Contains("/fiscalize")
            ? StubHttpServer.Json(200, """
                {"success":true,"fiscalCode":"FC-77","receiptNumber":77,
                 "receipt":{"invoiceNo":"INV-1001","receiptGlobalNo":77,"receiptCounter":1,
                            "fiscalDayNo":5,"receiptTotal":5.00}}
                """)
            : StubHttpServer.Json(200, """{"data":[],"total":0,"pages":1,"page":1,"limit":100}""");

    private static string Sale(
        string invoiceNumber = "INV-1001",
        string? on = null,
        string footer = "")
    {
        var text = $"""
            Store Demo
            USD Cash Sale
            Order No.: {invoiceNumber}
            Buyer Name: Acme Ltd
            Date: {on ?? DateTimeOffset.Now.ToString("dd/MM/yyyy")} 10:15
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            Fresh Milk 2.00 1.25 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            Payment: CASH
            """;

        return footer.Length == 0 ? text : text + "\n" + footer;
    }

    private sealed class Rig
    {
        private readonly StubHttpServer _server;

        public Rig(InterceptorConfig config, StubHttpServer server)
        {
            Printer = new FakePrinter();
            Ledger = new JobLedger(Path.Combine(config.ResolveDataDirectory(), "state"), NullLog.Instance);

            var client = new FiscalStackClient(config, NullLog.Instance);
            var chain = new FiscalChainStore(Path.Combine(config.ResolveDataDirectory(), "state"), NullLog.Instance);
            var offline = new OfflineReceiptService(config, chain, NullLog.Instance);
            var queue = new OfflineQueue(Path.Combine(config.ResolveDataDirectory(), "state"), NullLog.Instance);
            var sync = new OfflineSyncEngine(client, offline, queue, Ledger, NullLog.Instance);

            Pipeline = new PrintJobPipeline(
                config,
                new JobDecoder(config, NullLog.Instance),
                client,
                Ledger,
                new JobStore(config, NullLog.Instance),
                new PrintSnapshotStore(config, NullLog.Instance),
                new CompanyProfileService(config, NullLog.Instance),
                Printer,
                offline,
                queue,
                sync,
                NullLog.Instance);

            _server = server;
        }

        public PrintJobPipeline Pipeline { get; }
        public JobLedger Ledger { get; }
        public FakePrinter Printer { get; }

        public IReadOnlyList<StubRequest> Requests => _server.Requests;

        public IReadOnlyList<StubRequest> Posts =>
            Requests.Where(request => request.Method == "POST").ToList();

        public Task SendAsync(string text) =>
            Pipeline.ProcessAsync(
                new PrintJob(Encoding.UTF8.GetBytes(text), "127.0.0.1:52000", DateTimeOffset.Now),
                CancellationToken.None);
    }

    private sealed class FakePrinter : IReceiptPrinter
    {
        public List<ReceiptPrintModel> Models { get; } = new();

        public void Print(ReceiptPrintModel model) => Models.Add(model);
    }
}
