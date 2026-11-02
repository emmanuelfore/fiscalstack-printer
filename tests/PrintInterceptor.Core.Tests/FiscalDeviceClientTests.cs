using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class FiscalDeviceClientTests : IDisposable
{
    private StubHttpServer? _server;

    private IReadOnlyList<StubRequest> Requests => _server!.Requests;

    public void Dispose() => _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task ReadsTheLiveDeviceStatus()
    {
        using var client = Client(Start(StatusRoute));

        var result = await client.GetDeviceStatusAsync(CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var status = result.Status!;
        Assert.Equal("812", status.DeviceId);
        Assert.Equal("SN-001", status.SerialNumber);
        Assert.Equal("FiscalDayOpened", status.FiscalDayStatus);
        Assert.True(status.IsDayOpen);
        Assert.False(status.IsClosePending);
        Assert.Equal(5, status.FiscalDayNo);
        Assert.Equal(77, status.LastReceiptGlobalNo);
        Assert.Null(status.FiscalDayClosingErrorCode);
        Assert.Equal(2026, status.FiscalDayOpenedAt!.Value.Year);

        var request = Assert.Single(Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/zimra/device-status", request.Path);
        Assert.Equal("sk_test_example", request.ApiKey);
    }

    [Fact]
    public async Task CloseFailedDayCountsAsOpenForRetry()
    {
        using var client = Client(Start(path => path == "/api/zimra/device-status"
            ? Json(200, """
                {
                  "Code": "1",
                  "Message": "Success",
                  "DeviceID": "812",
                  "DeviceSerialNumber": "SN-001",
                  "FiscalDay": "5",
                  "Data": {
                    "fiscalDayStatus": "FiscalDayCloseFailed",
                    "lastFiscalDayNo": 5,
                    "lastReceiptGlobalNo": 77,
                    "fiscalDayClosingErrorCode": "MissingReceipts"
                  }
                }
                """)
            : Json(404, """{"message":"not found"}""")));

        var result = await client.GetDeviceStatusAsync(CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Status!.IsDayOpen);
        Assert.True(result.Status.IsClosePending);
        Assert.Equal("MissingReceipts", result.Status.FiscalDayClosingErrorCode);
    }

    [Fact]
    public async Task SurfacesAStatusError()
    {
        using var client = Client(Start(_ =>
            Json(400, """{"Code":"0","Message":"Company not registered with a fiscal device"}""")));

        var result = await client.GetDeviceStatusAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not registered", result.Error);
        Assert.Null(result.Status);
    }

    [Fact]
    public async Task ReadsTheRegistrationCard()
    {
        using var client = Client(Start(path => path == "/api/zimra/device-details"
            ? Json(200, """
                {
                  "Code": "1",
                  "Message": "Success",
                  "Data": {
                    "TIN": "200053040-8",
                    "BPN": "BPN-1",
                    "VAT": "87654321",
                    "COMPANYNAME": "Lekuka Retail",
                    "ADDRESS": "Maseru Mall",
                    "REGISTRATIONNUMBER": "812",
                    "SERIALNUMBER": "SN-001",
                    "LOGOURL": "https://example.test/logo.png"
                  }
                }
                """)
            : Json(404, """{"message":"not found"}""")));

        var result = await client.GetDeviceDetailsAsync(CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var card = result.Card!;
        Assert.Equal("200053040-8", card.Tin);
        Assert.Equal("87654321", card.Vat);
        Assert.Equal("BPN-1", card.Bpn);
        Assert.Equal("Lekuka Retail", card.CompanyName);
        Assert.Equal("Maseru Mall", card.Address);
        Assert.Equal("812", card.RegistrationNumber);
        Assert.Equal("SN-001", card.SerialNumber);
        Assert.Equal("https://example.test/logo.png", card.LogoUrl);
    }

    [Fact]
    public async Task OpensTheFiscalDay()
    {
        using var client = Client(Start(path => path == "/api/zimra/day/open"
            ? Json(200, """{"success":true,"message":"Fiscal day is already open","fiscalDayNo":5}""")
            : Json(404, """{"message":"not found"}""")));

        var result = await client.OpenFiscalDayAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Pending);
        Assert.Equal(5, result.FiscalDayNo);
        Assert.Equal("Fiscal day is already open", result.Message);

        var request = Assert.Single(Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/zimra/day/open", request.Path);
        Assert.Equal("sk_test_example", request.ApiKey);
    }

    [Fact]
    public async Task CloseReportsAPendingClosure()
    {
        using var client = Client(Start(_ => Json(202,
            """{"success":false,"message":"Closure submitted but not yet confirmed","fiscalDayStatus":"FiscalDayCloseInitiated"}""")));

        var result = await client.CloseFiscalDayAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Pending);
        Assert.Contains("not yet confirmed", result.Message);
        Assert.Equal("/api/zimra/day/close", Assert.Single(Requests).Path);
    }

    [Fact]
    public async Task CloseReportsTheGatewayErrorCode()
    {
        using var client = Client(Start(_ => Json(400, """
            {
              "message": "ZIMRA rejected closure: One or more receipts are missing from the sequence (Grey Error).",
              "fiscalDayStatus": "FiscalDayCloseFailed",
              "fiscalDayClosingErrorCode": "MissingReceipts"
            }
            """)));

        var result = await client.CloseFiscalDayAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Pending);
        Assert.Equal("MissingReceipts", result.ErrorCode);
        Assert.Contains("Grey Error", result.Message);
    }

    [Fact]
    public async Task CloseReportsSuccess()
    {
        using var client = Client(Start(_ =>
            Json(200, """{"success":true,"message":"Fiscal day 5 closed successfully","fiscalDayNo":5}""")));

        var result = await client.CloseFiscalDayAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, result.FiscalDayNo);
        Assert.Equal("Fiscal day 5 closed successfully", result.Message);
    }

    [Fact]
    public async Task TransportFailureIsReportedNotThrown()
    {
        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{StubHttpServer.FreePort()}/",
            ApiKey = "sk_test_example"
        };

        using var client = new FiscalStackClient(config, NullLog.Instance);
        var result = await client.GetDeviceStatusAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ValidationRejectionNamesTheFieldsThatFailed()
    {
        // A bare "Invalid request body" tells the operator nothing; the schema's
        // per-field complaints are the only actionable part of a 400.
        using var client = Client(Start(_ => Json(400, """
            {
              "error": "VALIDATION_ERROR",
              "message": "Invalid request body",
              "statusCode": 400,
              "details": [
                { "field": "currency", "message": "Invalid enum value. Expected 'USD' | 'ZWG', received 'LSL'" },
                { "field": "items.0.quantity", "message": "Quantity must be greater than 0" }
              ]
            }
            """)));

        var outcome = await client.FiscaliseAsync(new FiscalizeRequest { InvoiceNumber = "INV-1" }, CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Rejected, outcome.Status);
        Assert.Equal(400, outcome.HttpStatus);
        Assert.Contains("VALIDATION_ERROR", outcome.Error);
        Assert.Contains("currency: Invalid enum value", outcome.Error);
        Assert.Contains("items.0.quantity: Quantity must be greater than 0", outcome.Error);
    }

    [Fact]
    public async Task FiscalizationFailureCarriesTheGatewaysHint()
    {
        using var client = Client(Start(_ => Json(422, """
            {
              "error": "FISCALIZATION_FAILED",
              "message": "Fiscal day is not open",
              "statusCode": 422,
              "hint": "Ensure your ZIMRA device is registered and the fiscal day is open."
            }
            """)));

        var outcome = await client.FiscaliseAsync(new FiscalizeRequest { InvoiceNumber = "INV-2" }, CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Rejected, outcome.Status);
        Assert.Contains("Fiscal day is not open", outcome.Error);
        Assert.Contains("fiscal day is open", outcome.Error);
    }

    private FiscalStackClient Client(int port) => new(
        new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{port}/",
            ApiKey = "sk_test_example"
        },
        NullLog.Instance);

    private int Start(Func<string, StubResponse> respond)
    {
        _server = new StubHttpServer(respond);
        return _server.Port;
    }

    private static StubResponse StatusRoute(string path) => path == "/api/zimra/device-status"
        ? Json(200, """
            {
              "Code": "1",
              "Message": "Success",
              "DeviceID": "812",
              "DeviceSerialNumber": "SN-001",
              "FiscalDay": "5",
              "Data": {
                "fiscalDayStatus": "FiscalDayOpened",
                "lastFiscalDayNo": 5,
                "lastReceiptGlobalNo": 77,
                "fiscalDayOpenedAt": "2026-10-31T08:15:00.000Z",
                "fiscalDayClosingErrorCode": null
              }
            }
            """)
        : Json(404, """{"message":"not found"}""");

    private static StubResponse Json(int status, string body) => StubHttpServer.Json(status, body);
}
