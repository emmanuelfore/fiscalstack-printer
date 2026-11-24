using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class WatchedPdfServiceTests
{
    private sealed class RecordingProcessor : IJobProcessor
    {
        private readonly Channel<PrintJob> _jobs = Channel.CreateUnbounded<PrintJob>();

        public Task ProcessAsync(PrintJob job, CancellationToken cancellationToken)
        {
            _jobs.Writer.TryWrite(job);
            return Task.CompletedTask;
        }

        public async Task<PrintJob> NextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _jobs.Reader.ReadAsync(cts.Token);
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static InterceptorConfig ConfigFor(string dropDirectory) => new()
    {
        ListenAddress = "127.0.0.1",
        ListenPort = FreePort(),
        ReadIdleTimeoutSeconds = 1,
        Capture = new CaptureConfig
        {
            Enabled = true,
            PdfWatchDirectory = dropDirectory,
            StabilityMilliseconds = 1500
        }
    };

    private static string NewTempDirectory() =>
        Path.Combine(Path.GetTempPath(), "printinterceptor-watcher-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QueuesStablePdfAndMovesItToProcessed()
    {
        var directory = NewTempDirectory();
        Directory.CreateDirectory(directory);
        var processor = new RecordingProcessor();
        var config = ConfigFor(directory);
        await using var server = new PrintJobServer(processor, config, NullLog.Instance);
        var watcher = new WatchedPdfService(config, server, NullLog.Instance);

        try
        {
            await server.StartAsync(CancellationToken.None);
            await watcher.StartAsync(CancellationToken.None);

            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nwatched invoice\n%%EOF\n");
            var source = Path.Combine(directory, "invoice.pdf");
            await File.WriteAllBytesAsync(source, bytes);

            var job = await processor.NextAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(bytes, job.Data);
            Assert.StartsWith("file://", job.RemoteEndPoint);
            Assert.False(File.Exists(source));
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "processed"), "*.pdf"));
        }
        finally
        {
            await watcher.StopAsync();
            await server.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MovesNonPdfDropsToRejectedWithoutQueuingThem()
    {
        var directory = NewTempDirectory();
        Directory.CreateDirectory(directory);
        var processor = new RecordingProcessor();
        var config = ConfigFor(directory);
        await using var server = new PrintJobServer(processor, config, NullLog.Instance);
        var watcher = new WatchedPdfService(config, server, NullLog.Instance);

        try
        {
            await server.StartAsync(CancellationToken.None);
            await watcher.StartAsync(CancellationToken.None);

            var source = Path.Combine(directory, "not-a-pdf.pdf");
            await File.WriteAllTextAsync(source, "plain text");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!Directory.GetFiles(Path.Combine(directory, "rejected"), "*not-pdf-*.pdf").Any())
            {
                await Task.Delay(50, cts.Token);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await processor.NextAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            await watcher.StopAsync();
            await server.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
