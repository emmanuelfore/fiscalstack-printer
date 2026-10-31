using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.State;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class OfflineSyncEngineTests
{
    [Fact]
    public async Task RejectionPausesTheQueueDurably()
    {
        var directory = TempDirectory();
        await using var server = new StubServer(path =>
            path.EndsWith("/fiscalize", StringComparison.Ordinal)
                ? (400, "{\"message\":\"daily counter is not the next in sequence\"}")
                : (200, "{\"ok\":true}"));

        try
        {
            var log = NullLog.Instance;
            using var client = Client(server.Port, log);
            var queue = new OfflineQueue(directory, log);
            var item = Item("aaaa");
            Assert.True(queue.TryEnqueue(item, out _));

            var engine = Engine(client, queue, directory, log);
            var outcome = await engine.SyncNowAsync(CancellationToken.None);

            Assert.True(outcome.Paused);
            Assert.Equal(0, outcome.Synced);
            Assert.Contains("counter", outcome.Error);

            var reopened = new OfflineQueue(directory, log);
            Assert.True(reopened.IsPaused);
            Assert.False(reopened.HasPending);
            Assert.Equal("aaaa", reopened.CurrentPause!.JobHash);

            var ledger = new JobLedger(directory, log);
            Assert.Equal(LedgerStatus.Rejected, ledger.Get("aaaa")!.Status);

            engine.Resume();
            Assert.False(new OfflineQueue(directory, log).IsPaused);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PausedQueueDoesNotContactTheApi()
    {
        var directory = TempDirectory();
        var requests = 0;
        await using var server = new StubServer(_ =>
        {
            Interlocked.Increment(ref requests);
            return (200, "{}");
        });

        try
        {
            var log = NullLog.Instance;
            using var client = Client(server.Port, log);
            var queue = new OfflineQueue(directory, log);
            var item = Item("bbbb");
            Assert.True(queue.TryEnqueue(item, out _));
            queue.Pause(item, "the previous receipt was rejected");

            var engine = Engine(client, queue, directory, log);
            var outcome = await engine.SyncNowAsync(CancellationToken.None);

            Assert.True(outcome.Paused);
            Assert.Equal(0, requests);
            Assert.True(queue.HasPending);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FiscalStackClient Client(int port, ILog log) =>
        new(
            new InterceptorConfig
            {
                ApiEndpoint = $"http://127.0.0.1:{port}/",
                ApiKey = "sk_test_example"
            },
            log);

    private static OfflineSyncEngine Engine(
        FiscalStackClient client,
        OfflineQueue queue,
        string directory,
        ILog log)
    {
        var config = new InterceptorConfig();
        var offline = new OfflineReceiptService(config, new FiscalChainStore(directory, log), log);
        return new OfflineSyncEngine(client, offline, queue, new JobLedger(directory, log), log);
    }

    private static OfflineQueueItem Item(string hash) => new()
    {
        JobHash = hash,
        CreatedAt = DateTimeOffset.Now,
        Request = new FiscalizeRequest { Currency = "USD", InvoiceNumber = "INV-1" },
        ReceiptCounter = 4,
        ReceiptGlobalNo = 9,
        FiscalDayNo = 3
    };

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "pi-sync-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Minimal loopback HTTP server. A raw socket avoids the HttpListener URL
    /// ACL that fails for non-admin test runs on Windows.
    /// </summary>
    private sealed class StubServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<string, (int Status, string Body)> _respond;

        public StubServer(Func<string, (int Status, string Body)> respond)
        {
            _respond = respond;
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

                    var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                    var (status, responseBody) = _respond(path);
                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    var reason = status == 200 ? "OK" : "Error";
                    var head =
                        $"HTTP/1.1 {status} {reason}\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {bytes.Length}\r\n" +
                        "Connection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(bytes);
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
