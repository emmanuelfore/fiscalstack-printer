using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class CompanyProfileServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pi-company-" + Guid.NewGuid().ToString("N"));

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
    public void CachedProfileWinsAndFallsBackPerField()
    {
        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(
            Path.Combine(state, "company-profile.json"),
            """
            {
              "profile": {
                "id": 7,
                "name": "Lekuka Retail",
                "tin": "200053040-8",
                "vatNumber": "",
                "address": "Maseru Mall",
                "city": "Maseru",
                "phone": "",
                "email": "accounts@lekuka.co.ls",
                "logoUrl": "https://example.test/logo.png"
              },
              "logoFile": null,
              "fetchedAt": "2026-10-30T10:00:00+02:00"
            }
            """);

        var service = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig
            {
                Name = "Local Fallback",
                Tin = "LOCAL-TIN",
                VatNumber = "12345678",
                Address = "Local address",
                Phone = "+266 2222 0000",
                Email = "local@example.test"
            }
        });

        Assert.True(service.IsProvisioned);
        Assert.Equal("Lekuka Retail", service.Name);
        Assert.Equal("200053040-8", service.Tin);
        Assert.Equal("12345678", service.VatNumber);      // empty in profile -> local
        Assert.Equal("+266 2222 0000", service.Phone);    // empty in profile -> local
        Assert.Equal("accounts@lekuka.co.ls", service.Email);
        Assert.Equal("Maseru Mall, Maseru", service.Address);
        Assert.Null(service.ResolveLogoPath(null));
    }

    [Fact]
    public void AddressFallsBackToCityOrLocalAddress()
    {
        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);

        File.WriteAllText(
            Path.Combine(state, "company-profile.json"),
            """{ "profile": { "address": "", "city": "Maseru" } }""");
        var cityOnly = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig { Address = "Local address" }
        });
        Assert.Equal("Maseru", cityOnly.Address);

        File.WriteAllText(
            Path.Combine(state, "company-profile.json"),
            """{ "profile": { "address": "", "city": "" } }""");
        var empty = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig { Address = "Local address" }
        });
        Assert.Equal("Local address", empty.Address);

        File.WriteAllText(
            Path.Combine(state, "company-profile.json"),
            """{ "profile": { "address": "12 Maseru Mall, Maseru", "city": "Maseru" } }""");
        var composed = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory }
        });
        Assert.Equal("12 Maseru Mall, Maseru", composed.Address);
    }

    [Fact]
    public void WithoutCacheEverythingFallsBackToLocalConfig()
    {
        var service = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig
            {
                Name = "Local Store",
                Tin = "LOCAL-TIN",
                Address = "1 Main Street"
            }
        });

        Assert.False(service.IsProvisioned);
        Assert.Equal("Local Store", service.Name);
        Assert.Equal("LOCAL-TIN", service.Tin);
        Assert.Equal("1 Main Street", service.Address);
        Assert.Equal("C:\\logos\\local.png", service.ResolveLogoPath("C:\\logos\\local.png"));
    }

    [Fact]
    public void CorruptCacheIsIgnored()
    {
        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "company-profile.json"), "{not json");

        var service = Service(new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory },
            Company = new CompanyConfig { Name = "Local Store" }
        });

        Assert.False(service.IsProvisioned);
        Assert.Equal("Local Store", service.Name);
    }

    [Fact]
    public async Task RefreshStoresLogoByMagicBytesAndReplacesStaleFormat()
    {
        await using var server = new StubServer();

        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{server.Port}/",
            ApiKey = "sk_test_example",
            Storage = new StorageConfig { DataDirectory = _directory }
        };

        using var client = new FiscalStackClient(config, NullLog.Instance);
        var service = Service(config);

        Assert.True(await service.RefreshAsync(client, CancellationToken.None));
        Assert.True(service.IsProvisioned);
        Assert.Equal("Provisioned Store", service.Name);
        Assert.Equal("Maseru Mall, Maseru", service.Address);

        var logo = service.ResolveLogoPath(null);
        Assert.NotNull(logo);
        Assert.EndsWith("company-logo.png", logo);
        Assert.True(File.Exists(logo));

        // The cache restores the identity (and the logo) after a restart.
        var restarted = Service(config);
        Assert.True(restarted.IsProvisioned);
        Assert.Equal("Provisioned Store", restarted.Name);
        Assert.EndsWith("company-logo.png", restarted.ResolveLogoPath(null));

        // A later refresh that serves a JPEG must move to .jpg and drop the PNG.
        server.UseJpeg = true;
        Assert.True(await service.RefreshAsync(client, CancellationToken.None));
        var jpeg = service.ResolveLogoPath(null);
        Assert.EndsWith("company-logo.jpg", jpeg);
        Assert.True(File.Exists(jpeg));
        Assert.False(File.Exists(Path.Combine(_directory, "state", "company-logo.png")));
    }

    [Fact]
    public async Task RefreshKeepsLocalLogoWhenTheDownloadIsNotAnImage()
    {
        await using var server = new StubServer { UseJpeg = false, LogoIsImage = false };

        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{server.Port}/",
            ApiKey = "sk_test_example",
            Storage = new StorageConfig { DataDirectory = _directory }
        };

        using var client = new FiscalStackClient(config, NullLog.Instance);
        var service = Service(config);

        Assert.True(await service.RefreshAsync(client, CancellationToken.None));
        Assert.Null(service.ResolveLogoPath(null));
        Assert.Equal("C:\\logos\\local.png", service.ResolveLogoPath("C:\\logos\\local.png"));
    }

    [Fact]
    public async Task FailedRefreshKeepsTheCachedIdentity()
    {
        await using var server = new StubServer { Status = 500 };

        var config = new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{server.Port}/",
            ApiKey = "sk_test_example",
            Storage = new StorageConfig { DataDirectory = _directory }
        };

        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(
            Path.Combine(state, "company-profile.json"),
            """{ "profile": { "name": "Cached Store", "tin": "CACHED-TIN" } }""");

        using var client = new FiscalStackClient(config, NullLog.Instance);
        var service = Service(config);

        Assert.False(await service.RefreshAsync(client, CancellationToken.None));
        Assert.Equal("Cached Store", service.Name);
        Assert.Equal("CACHED-TIN", service.Tin);
    }

    private static CompanyProfileService Service(InterceptorConfig config) =>
        new(config, NullLog.Instance);

    /// <summary>
    /// Loopback HTTP stub for /api/v1/company and the logo download. A raw
    /// socket avoids the HttpListener URL ACL that fails for non-admin runs.
    /// </summary>
    private sealed class StubServer : IAsyncDisposable
    {
        private static readonly byte[] PngBytes =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00
        };

        private static readonly byte[] JpegBytes =
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10
        };

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public StubServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public int Status { get; init; } = 200;
        public bool UseJpeg { get; set; }
        public bool LogoIsImage { get; init; } = true;

        private Task AcceptLoopAsync() => Task.Run(async () =>
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
        });

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                    var requestLine = await reader.ReadLineAsync() ?? "";
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        // Drain request headers.
                    }

                    var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                    var (status, contentType, body) = Respond(path);
                    var head =
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch
                {
                    // The client aborted; nothing to do in a stub.
                }
            }
        }

        private (int Status, string ContentType, byte[] Body) Respond(string path)
        {
            if (Status != 200)
            {
                return (Status, "application/json", Encoding.UTF8.GetBytes("{\"message\":\"failed\"}"));
            }

            if (path.Contains("logo", StringComparison.OrdinalIgnoreCase))
            {
                return LogoIsImage
                    ? (200, UseJpeg ? "image/jpeg" : "image/png", UseJpeg ? JpegBytes : PngBytes)
                    : (200, "text/html", Encoding.UTF8.GetBytes("<html>not an image</html>"));
            }

            if (path.Contains("company", StringComparison.OrdinalIgnoreCase))
            {
                var logoUrl = $"http://127.0.0.1:{Port}/logo";
                var json =
                    "{" +
                    "\"id\":7," +
                    "\"name\":\"Provisioned Store\"," +
                    "\"tin\":\"200053040-8\"," +
                    "\"vatNumber\":\"12345678\"," +
                    "\"address\":\"Maseru Mall\"," +
                    "\"city\":\"Maseru\"," +
                    "\"phone\":\"+266 2222 0000\"," +
                    "\"email\":\"accounts@lekuka.co.ls\"," +
                    $"\"logoUrl\":\"{logoUrl}\"" +
                    "}";
                return (200, "application/json", Encoding.UTF8.GetBytes(json));
            }

            return (404, "application/json", Encoding.UTF8.GetBytes("{}"));
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
