using System.Text;
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
/// The safety of the reconciliation pass, which is the part a till operator cannot check by
/// hand: a receipt whose outcome is unknown may already exist in ZIMRA, so it is only ever
/// re-sent when the server says it is missing, and only the exact body that timed out goes out.
/// </summary>
public sealed class ReconcilePipelineTests : IDisposable
{
    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "pi-reconcile-" + Guid.NewGuid().ToString("N"));

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
    public async Task TheServersProofIsAdoptedAndTheFiscalCopyPrinted()
    {
        var rig = Harness(_ => InvoiceList(InvoiceRow("INV-1001", "5.00")));
        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.Adopted);
        Assert.Equal(LedgerStatus.Fiscalised, rig.Ledger.Get("hash-1")!.Status);
        Assert.Equal("FC-9", rig.Ledger.Get("hash-1")!.FiscalCode);

        var model = Assert.Single(rig.Printer.Models);
        Assert.Equal("FC-9", model.FiscalCode);
        Assert.Equal("https://verify.test/FC-9", model.QrCodeUrl);
        Assert.Equal(77, model.ReceiptGlobalNo);

        // The list read carries no verification code; the copy says so by printing nothing.
        Assert.Null(model.VerificationCode);
        Assert.Empty(rig.Posts);
    }

    [Fact]
    public async Task AMissingReceiptIsReSentAsTheExactBodyThatTimedOut()
    {
        const string body = """
            {"items":[{"name":"Bread Loaf","quantity":1,"unitPrice":2.5,"taxType":"STANDARD"}],"invoiceNumber":"INV-1001"}
            """;

        var rig = Harness(target => target.Contains("/fiscalize")
            ? StubHttpServer.Json(200, """
                {"success":true,"fiscalCode":"FC-78","receiptNumber":78,
                 "receipt":{"invoiceNo":"INV-1001","receiptGlobalNo":78,"receiptCounter":4,"fiscalDayNo":5,"receiptTotal":5.00}}
                """)
            : InvoiceList(InvoiceRow("INV-1000", "9.00")));

        rig.Seed("hash-1", "INV-1001", 5.00m, body);

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.Resent);
        Assert.Equal(body, Assert.Single(rig.Posts).Body);
        Assert.Equal(LedgerStatus.Fiscalised, rig.Ledger.Get("hash-1")!.Status);
        Assert.Equal("FC-78", rig.Ledger.Get("hash-1")!.FiscalCode);
        Assert.Equal("FC-78", Assert.Single(rig.Printer.Models).FiscalCode);
    }

    [Fact]
    public async Task AListThatCannotBeReadIsNotTreatedAsAMissingReceipt()
    {
        // HTTP 500 on the list says nothing about the sale, so the pass must not conclude
        // "the receipt never happened" and send a second one.
        var rig = Harness(_ => StubHttpServer.Json(500, """{"message":"Failed to list invoices"}"""));

        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.StillUnknown);
        Assert.Empty(rig.Posts);
        Assert.Empty(rig.Printer.Models);
        Assert.Equal(LedgerStatus.AmbiguousFailure, rig.Ledger.Get("hash-1")!.Status);
    }

    [Fact]
    public async Task NothingIsReSentWithoutTheBodyThatWentOutTheFirstTime()
    {
        var rig = Harness(retainJobs: false);
        rig.Seed("hash-1", "INV-1001", 5.00m, null);

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.SetAside);
        Assert.Empty(rig.Posts);

        var entry = rig.Ledger.Get("hash-1")!;
        Assert.Equal(LedgerStatus.NeedsReview, entry.Status);
        Assert.Contains("storage.retainJobs", entry.Error ?? "");
    }

    [Fact]
    public async Task WithAutoResendOffOnlyTheServersOwnProofIsUsed()
    {
        var rig = Harness(_ => InvoiceList(InvoiceRow("INV-1000", "9.00")));
        rig.Reconcile.AutoResend = false;
        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.StillUnknown);
        Assert.Empty(rig.Posts);
        Assert.Equal(LedgerStatus.AmbiguousFailure, rig.Ledger.Get("hash-1")!.Status);
    }

    [Fact]
    public async Task ADuplicateAnswerMeansTheReceiptLandedAfterTheListWasRead()
    {
        // The gateway refuses a number it already has. That is not a rejection to report but a
        // receipt to go and fetch, because the timed-out request is exactly what created it.
        var reads = 0;
        var rig = Harness(target =>
        {
            if (target.Contains("/fiscalize"))
            {
                return StubHttpServer.Json(409,
                    """{"error":"DUPLICATE_INVOICE","message":"Invoice number 'INV-1001' already exists."}""");
            }

            reads++;
            return reads == 1
                ? InvoiceList(InvoiceRow("INV-1000", "9.00"))
                : InvoiceList(InvoiceRow("INV-1001", "5.00"));
        });

        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.Adopted);
        Assert.Equal(2, reads);
        Assert.Equal("FC-9", rig.Ledger.Get("hash-1")!.FiscalCode);
        Assert.Single(rig.Posts);
    }

    [Fact]
    public async Task AReceiptSentWithoutANumberIsNeverReSent()
    {
        // The gateway auto-numbers a receipt that has none, so a replay would create a second
        // sale under a different number — the one case its duplicate check cannot catch.
        var rig = Harness(_ => InvoiceList(InvoiceRow("INV-1000", "9.00")));
        rig.Seed("hash-1", null, 5.00m, """{"items":[{"name":"Bread Loaf","quantity":1,"unitPrice":2.5}]}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.StillUnknown);
        Assert.Empty(rig.Requests);
        Assert.Equal(LedgerStatus.AmbiguousFailure, rig.Ledger.Get("hash-1")!.Status);
    }

    [Fact]
    public async Task SomeoneElsesReceiptIsNotAdoptedAsOurs()
    {
        var rig = Harness(_ => InvoiceList(InvoiceRow("INV-1001", "9000.00")));

        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        var report = await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, report.SetAside);
        Assert.Empty(rig.Posts);
        Assert.Empty(rig.Printer.Models);

        var entry = rig.Ledger.Get("hash-1")!;
        Assert.Equal(LedgerStatus.NeedsReview, entry.Status);
        Assert.DoesNotContain("FC-9", entry.FiscalCode ?? "");
    }

    [Fact]
    public async Task ALostReceiptIsLeftForTheOperatorAfterTheBudget()
    {
        var rig = Harness(_ => StubHttpServer.Json(500, """{"message":"Failed to list invoices"}"""));
        rig.Reconcile.MaxAttempts = 2;
        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        await rig.Pipeline.ReconcileAsync(CancellationToken.None);
        await rig.Pipeline.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, rig.Ledger.Get("hash-1")!.ReconcileAttempts);
        Assert.Equal(0, (await rig.Pipeline.ReconcileAsync(CancellationToken.None)).Scanned);

        // The invoice number stays blocked: a job nobody can settle must not become a free pass
        // for a second fiscal receipt.
        Assert.Equal(LedgerStatus.AmbiguousFailure, rig.Ledger.Get("hash-1")!.Status);
        Assert.Equal(1, (await rig.Pipeline.ReconcileAsync(CancellationToken.None, force: true)).Scanned);
    }

    [Fact]
    public async Task ADisabledPassRunsOnlyWhenTheOperatorAsks()
    {
        var rig = Harness(_ => InvoiceList(InvoiceRow("INV-1001", "5.00")));
        rig.Reconcile.Enabled = false;
        rig.Seed("hash-1", "INV-1001", 5.00m, """{"invoiceNumber":"INV-1001"}""");

        Assert.Equal(0, (await rig.Pipeline.ReconcileAsync(CancellationToken.None)).Scanned);
        Assert.Empty(rig.Requests);

        Assert.Equal(1, (await rig.Pipeline.ReconcileAsync(CancellationToken.None, force: true)).Adopted);
    }

    private Rig Harness(Func<string, StubResponse>? respond = null, bool retainJobs = true)
    {
        // The default answer is an empty list: the receipt is not there, which is the case most
        // of these tests are about. Individual tests route the list and the replay themselves.
        var server = new StubHttpServer(respond ?? (_ => InvoiceList()));
        _servers.Add(server);

        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{server.Port}/api/v1/",
            ApiKey = "sk_test_example",
            Storage = new StorageConfig { DataDirectory = _data, RetainJobs = retainJobs },
            Printing = new PrintingConfig { Enabled = true, RetryCount = 0 }
        };

        return new Rig(config, server, _data);
    }

    private static StubResponse InvoiceList(params string[] rows) =>
        StubHttpServer.Json(200,
            $$"""
              {"data":[{{string.Join(",", rows)}}],"total":{{rows.Length}},"pages":1,"page":1,"limit":100}
              """);

    private static string InvoiceRow(string invoiceNumber, string total) =>
        $$"""
          {"id":12,"invoiceNumber":"{{invoiceNumber}}","status":"FISCALISED","transactionType":"FiscalInvoice",
           "issueDate":"2026-10-29","currency":"USD","total":{{total}},"fiscalCode":"FC-9",
           "qrCodeData":"https://verify.test/FC-9","receiptGlobalNo":77,"receiptCounter":3,"fiscalDayNo":5}
          """;

    private sealed class Rig
    {
        private readonly JobStore _store;
        private readonly StubHttpServer _server;

        public Rig(InterceptorConfig config, StubHttpServer server, string dataDirectory)
        {
            Config = config;
            _server = server;
            Printer = new FakePrinter();
            Ledger = new JobLedger(Path.Combine(dataDirectory, "state"), NullLog.Instance);
            _store = new JobStore(config, NullLog.Instance);

            var client = new FiscalStackClient(config, NullLog.Instance);
            var chain = new FiscalChainStore(Path.Combine(dataDirectory, "state"), NullLog.Instance);
            var offline = new OfflineReceiptService(config, chain, NullLog.Instance);
            var queue = new OfflineQueue(Path.Combine(dataDirectory, "state"), NullLog.Instance);
            var sync = new OfflineSyncEngine(client, offline, queue, Ledger, NullLog.Instance);

            Pipeline = new PrintJobPipeline(
                config,
                new JobDecoder(config, NullLog.Instance),
                client,
                Ledger,
                _store,
                new PrintSnapshotStore(config, NullLog.Instance),
                new CompanyProfileService(config, NullLog.Instance),
                Printer,
                offline,
                queue,
                sync,
                NullLog.Instance);
        }

        public InterceptorConfig Config { get; }
        public ReconcileConfig Reconcile => Config.Reconcile;
        public PrintJobPipeline Pipeline { get; }
        public JobLedger Ledger { get; }
        public FakePrinter Printer { get; }

        public IReadOnlyList<StubRequest> Requests => _server.Requests;

        public IReadOnlyList<StubRequest> Posts =>
            Requests.Where(request => request.Method == "POST").ToList();

        /// <summary>
        /// A job whose fiscalisation timed out: the ledger says the outcome is unknown, the
        /// archive holds what the till printed and the body that went out.
        /// </summary>
        public void Seed(string hash, string? invoiceNumber, decimal total, string? requestBody)
        {
            var sourceFile = _store.Save(
                hash,
                new DecodedJob(true, JobFormat.Text, Sale, null, null),
                Encoding.UTF8.GetBytes(Sale));

            if (requestBody is not null)
            {
                _store.SaveRequest(sourceFile, requestBody);
            }

            Ledger.Claim(hash, invoiceNumber, "20261029", sourceFile, total, "USD", "FiscalInvoice");
            Ledger.MarkFailure(hash, LedgerStatus.AmbiguousFailure, "The request timed out after 30s.");
        }

        private const string Sale = """
            Store Demo
            USD Cash Sale
            Order No.: INV-1001
            Buyer Name: Acme Ltd
            Date: 29/10/2026 10:15
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            Fresh Milk 2.00 1.25 2.50
            --------------------------------
            Items count: 2
            Total: 5.00
            Payment: CASH
            """;
    }

    private sealed class FakePrinter : IReceiptPrinter
    {
        public List<ReceiptPrintModel> Models { get; } = new();

        public void Print(ReceiptPrintModel model) => Models.Add(model);
    }
}
