using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// The two reads a reconciliation depends on: the tenant's invoice list, which answers
/// "did this receipt make it?", and the raw replay of a body that already timed out.
/// </summary>
public sealed class ReconcileClientTests : IDisposable
{
    private readonly List<StubHttpServer> _servers = new();
    private StubHttpServer? _last;

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _servers.Clear();
    }

    [Fact]
    public async Task FindsTheReceiptOnTheFirstPage()
    {
        using var client = Client(Start(_ => Json(200, Page(
            1,
            1,
            """
            {"id":12,"invoiceNumber":"INV-9","status":"FISCALISED","transactionType":"FiscalInvoice",
             "issueDate":"2026-11-02","currency":"USD","subtotal":4.35,"taxAmount":0.65,"total":5.00,
             "fiscalCode":"FC-9","qrCodeData":"https://verify.test/FC-9","receiptGlobalNo":77,
             "receiptCounter":3,"fiscalDayNo":5,"companyId":1,"customerId":null,"notes":null}
            """))));

        var lookup = await client.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Found, lookup.Status);
        Assert.Null(lookup.Error);
        var invoice = lookup.Invoice!;
        Assert.Equal("INV-9", invoice.InvoiceNumber);
        Assert.Equal("FC-9", invoice.FiscalCode);
        Assert.Equal("https://verify.test/FC-9", invoice.QrCode);
        Assert.Equal(5.00m, invoice.Total);
        Assert.Equal("USD", invoice.Currency);
        Assert.Equal(77, invoice.ReceiptGlobalNo);
        Assert.Equal(3, invoice.ReceiptCounter);
        Assert.Equal(5, invoice.FiscalDayNo);

        var request = Assert.Single(Requests);
        Assert.Equal("/api/v1/invoices", request.Path);
        Assert.Equal("page=1&limit=100", request.Query);
        Assert.Equal("sk_test_example", request.ApiKey);
    }

    [Fact]
    public async Task AdoptsWhatTheListReadIntoTheShapeThePrinterAlreadyKnows()
    {
        using var client = Client(Start(_ => Json(200, Page(1, 1, Row("INV-9", total: "5.00")))));

        var lookup = await client.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        var response = lookup.Invoice!.ToResponse();
        Assert.True(response.Success);
        Assert.Equal("FC-9", response.FiscalCode);
        Assert.Equal(77, response.ReceiptNumber);
        Assert.Equal("INV-9", response.Receipt!.InvoiceNo);
        Assert.Equal(5.00m, response.Receipt.ReceiptTotal);
        Assert.Equal(new DateTime(2026, 11, 2), response.Receipt.ReceiptDate!.Value.DateTime);

        // The list carries no verification code, and inventing one would be worse than omitting it.
        Assert.Null(response.VerificationCode);
    }

    [Fact]
    public async Task WalksThePagesUntilTheEndOfTheList()
    {
        using var client = Client(Start(target => target.Contains("page=3")
            ? Json(200, Page(3, 3, Row("INV-9")))
            : Json(200, Page(3, 3, Row("INV-10"), Row("INV-8")))));

        var lookup = await client.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Found, lookup.Status);
        Assert.Equal(3, lookup.PagesRead);
        Assert.Equal(
            new[] { "page=1&limit=100", "page=2&limit=100", "page=3&limit=100" },
            Requests.Select(request => request.Query));
    }

    [Fact]
    public async Task ReportsAbsentOnceTheListIsExhausted()
    {
        using var client = Client(Start(_ => Json(200, Page(1, 1, Row("INV-10"), Row("INV-8")))));

        var lookup = await client.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Absent, lookup.Status);
        Assert.False(lookup.WindowExhausted);
        Assert.Null(lookup.Invoice);
        Assert.Single(Requests);
    }

    [Fact]
    public async Task StopsAtTheConfiguredWindowAndSaysSo()
    {
        // The walk is bounded by reconcile.scanPages, so an answer of "not there" has to admit
        // that it only read as far as it was told to.
        using var client = Client(Start(_ => Json(200, Page(1, 40, Row("INV-10")))));

        var lookup = await client.FindInvoiceAsync("INV-9", 2, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Absent, lookup.Status);
        Assert.True(lookup.WindowExhausted);
        Assert.Equal(2, lookup.PagesRead);
        Assert.Equal(2, Requests.Count);
    }

    [Fact]
    public async Task AnUnreadableListIsNeverAnAnswerAboutTheReceipt()
    {
        using var unavailable = Client(Start(_ => Json(500, """{"message":"Failed to list invoices"}""")));
        var failed = await unavailable.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Failed, failed.Status);
        Assert.Contains("HTTP 500", failed.Error);

        using var garbled = Client(Start(_ => new StubResponse(200, "text/html",
            System.Text.Encoding.UTF8.GetBytes("<html>login</html>"))));
        var unreadable = await garbled.FindInvoiceAsync("INV-9", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Failed, unreadable.Status);
    }

    [Fact]
    public async Task NothingToLookForFailsWithoutTouchingTheServer()
    {
        using var client = Client(Start(_ => Json(200, Page(1, 1, Row("INV-9")))));

        var lookup = await client.FindInvoiceAsync(" ", 10, CancellationToken.None);

        Assert.Equal(InvoiceLookupStatus.Failed, lookup.Status);
        Assert.Empty(Requests);
    }

    [Fact]
    public async Task ACounterTheServerLeftEmptyStaysEmpty()
    {
        using var client = Client(Start(_ => Json(200, Page(1, 1,
            """
            {"id":13,"invoiceNumber":"INV-9","status":"FISCALISED","transactionType":"FiscalInvoice",
             "issueDate":"2026-11-02","currency":"USD","total":5,"fiscalCode":"FC-9",
             "qrCodeData":null,"receiptGlobalNo":null,"receiptCounter":null,"fiscalDayNo":null}
            """))));

        var invoice = (await client.FindInvoiceAsync("INV-9", 10, CancellationToken.None)).Invoice!;

        Assert.Null(invoice.QrCode);
        Assert.Null(invoice.ReceiptGlobalNo);
        Assert.Null(invoice.ReceiptCounter);
        Assert.Null(invoice.FiscalDayNo);
    }

    [Fact]
    public async Task ReplaysTheStoredBodyByteForByte()
    {
        // Reconciling has to send what the gateway already saw, so the client must not
        // re-serialise the request on the way out.
        const string stored = """
            {"items":[{"name":"Bread","quantity":1,"unitPrice":2.5}],"invoiceNumber":"INV-9"}
            """;
        using var client = Client(Start(_ => Json(200,
            """{"success":true,"fiscalCode":"FC-9","receiptNumber":77,"receipt":{"invoiceNo":"INV-9"}}""")));

        var outcome = await client.FiscaliseRawAsync(stored, CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Success, outcome.Status);
        var request = Assert.Single(Requests);
        Assert.Equal("/api/v1/fiscalize", request.Path);
        Assert.Equal(stored, request.Body);
        Assert.Equal("sk_test_example", request.ApiKey);
    }

    [Fact]
    public async Task ADuplicateReplayIsReportedAsTheStatusTheCallerCanActOn()
    {
        using var client = Client(Start(_ => Json(409,
            """{"error":"DUPLICATE_INVOICE","message":"Invoice number 'INV-9' already exists."}""")));

        var outcome = await client.FiscaliseRawAsync("""{"invoiceNumber":"INV-9"}""", CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Rejected, outcome.Status);
        Assert.Equal(409, outcome.HttpStatus);
        Assert.Contains("already exists", outcome.Error);
    }

    private FiscalStackClient Client(int port) => new(
        new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{port}/api/v1/",
            ApiKey = "sk_test_example"
        },
        NullLog.Instance);

    private int Start(Func<string, StubResponse> respond)
    {
        var server = new StubHttpServer(respond);
        _servers.Add(server);
        _last = server;
        return server.Port;
    }

    private IReadOnlyList<StubRequest> Requests => _last!.Requests;

    private static StubResponse Json(int status, string body) => StubHttpServer.Json(status, body);

    /// <summary>One page of the tenant's invoice list, as the gateway returns it.</summary>
    private static string Page(int page, int pages, params string[] rows) =>
        $$"""
          {"data":[{{string.Join(",", rows)}}],"total":{{pages * 100}},"pages":{{pages}},"page":{{page}},"limit":100}
          """;

    private static string Row(string invoiceNumber, string total = "5.00") =>
        $$"""
          {"id":12,"invoiceNumber":"{{invoiceNumber}}","status":"FISCALISED","transactionType":"FiscalInvoice",
           "issueDate":"2026-11-02","currency":"USD","total":{{total}},"fiscalCode":"FC-9",
           "qrCodeData":"https://verify.test/FC-9","receiptGlobalNo":77,"receiptCounter":3,"fiscalDayNo":5}
          """;
}
