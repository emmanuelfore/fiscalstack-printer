using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class FiscalDeviceClientTests : IDisposable
{
    private readonly List<Request> _requests = new();
    private StubServer? _server;

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

        var request = Assert.Single(_requests);
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

        var request = Assert.Single(_requests);
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
        Assert.Equal("/api/zimra/day/close", Assert.Single(_requests).Path);
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
            ApiEndpoint = $"http://127.0.0.1:{FreePort()}/",
            ApiKey = "sk_test_example"
        };

        using var client = new FiscalStackClient(config, NullLog.Instance);
        var result = await client.GetDeviceStatusAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    private FiscalStackClient Client(int port) => new(
        new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{port}/",
            ApiKey = "sk_test_example"
        },
        NullLog.Instance);

    private int Start(Func<string, Response> respond)
    {
        _server = new StubServer(respond, _requests);
        return _server.Port;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Response StatusRoute(string path) => path == "/api/zimra/device-status"
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

    private static Response Json(int status, string body) =>
        new(status, "application/json", Encoding.UTF8.GetBytes(body));

    public sealed record Request(string Method, string Path, string? ApiKey);

    private sealed record Response(int Status, string ContentType, byte[] Body);

    /// <summary>
    /// Minimal loopback HTTP server. A raw socket avoids the HttpListener URL
    /// ACL that fails for non-admin test runs on Windows.
    /// </summary>
    private sealed class StubServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<string, Response> _respond;
        private readonly List<Request> _requests;

        public StubServer(Func<string, Response> respond, List<Request> requests)
        {
            _respond = respond;
            _requests = requests;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                    var requestLine = await reader.ReadLineAsync() ?? "";
                    var parts = requestLine.Split(' ');
                    var method = parts.ElementAtOrDefault(0) ?? "";
                    var path = parts.ElementAtOrDefault(1) ?? "/";
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        var separator = line.IndexOf(':');
                        if (separator > 0)
                        {
                            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                        }
                    }

                    if (headers.TryGetValue("Content-Length", out var lengthText) &&
                        int.TryParse(lengthText, out var length) && length > 0)
                    {
                        var body = new char[length];
                        var read = 0;
                        while (read < length)
                        {
                            var chunk = await reader.ReadAsync(body.AsMemory(read, length - read));
                            if (chunk == 0)
                            {
                                break;
                            }

                            read += chunk;
                        }
                    }

                    lock (_requests)
                    {
                        _requests.Add(new Request(
                            method,
                            path.Split('?')[0],
                            headers.TryGetValue("X-API-Key", out var key) ? key : null));
                    }

                    var response = _respond(path.Split('?')[0]);
                    var head =
                        $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n" +
                        $"Content-Type: {response.ContentType}\r\n" +
                        $"Content-Length: {response.Body.Length}\r\n" +
                        "Connection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(response.Body);
                    await stream.FlushAsync();
                }
                catch
                {
                    // The client aborted; nothing to do in a stub.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch
            {
                // Ignored while shutting the stub down.
            }

            _cts.Dispose();
        }
    }
}
