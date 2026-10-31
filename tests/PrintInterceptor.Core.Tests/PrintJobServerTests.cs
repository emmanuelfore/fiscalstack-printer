using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class PrintJobServerTests
{
    private sealed class RecordingProcessor : IJobProcessor
    {
        private readonly Channel<PrintJob> _jobs = Channel.CreateUnbounded<PrintJob>();

        public ValueTask<PrintJob> NextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return _jobs.Reader.ReadAsync(cts.Token);
        }

        public Task ProcessAsync(PrintJob job, CancellationToken cancellationToken)
        {
            _jobs.Writer.TryWrite(job);
            return Task.CompletedTask;
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

    private static InterceptorConfig ConfigFor(int port) => new()
    {
        ListenAddress = "127.0.0.1",
        ListenPort = port,
        ReadIdleTimeoutSeconds = 1
    };

    private static async Task SendAsync(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var bytes = Encoding.ASCII.GetBytes(payload);
        await client.GetStream().WriteAsync(bytes);
        await client.GetStream().FlushAsync();
    }

    [Fact]
    public async Task ReceivesRawJobOverTcp()
    {
        var port = FreePort();
        var processor = new RecordingProcessor();
        await using var server = new PrintJobServer(processor, ConfigFor(port), NullLog.Instance);
        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);

        const string payload = "Store Demo\nOrder No.: INV-1001\nTotal: 5.00\n";
        await SendAsync(port, payload);

        var job = await processor.NextAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payload, Encoding.ASCII.GetString(job.Data));
        Assert.StartsWith("127.0.0.1:", job.RemoteEndPoint);
    }

    [Fact]
    public async Task QueuesEachConnectionAsItsOwnJob()
    {
        var port = FreePort();
        var processor = new RecordingProcessor();
        await using var server = new PrintJobServer(processor, ConfigFor(port), NullLog.Instance);
        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);

        await SendAsync(port, "first receipt\n");
        await SendAsync(port, "second receipt\n");

        var first = await processor.NextAsync(TimeSpan.FromSeconds(10));
        var second = await processor.NextAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("first receipt\n", Encoding.ASCII.GetString(first.Data));
        Assert.Equal("second receipt\n", Encoding.ASCII.GetString(second.Data));
    }
}
