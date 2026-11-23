using System.Net;
using System.Net.Sockets;
using System.Reflection;
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

    private sealed class CancellableFirstProcessor : IJobProcessor
    {
        private readonly Channel<PrintJob> _processed = Channel.CreateUnbounded<PrintJob>();
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstStarted => _firstStarted.Task;
        public CancellationToken FirstToken { get; private set; }
        public int CallCount => Volatile.Read(ref _calls);

        public async Task ProcessAsync(PrintJob job, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstToken = cancellationToken;
                _firstStarted.TrySetResult();
                // Mimics an HTTP call that observes application cancellation.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            _processed.Writer.TryWrite(job);
        }

        public ValueTask<PrintJob> NextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return _processed.Reader.ReadAsync(cts.Token);
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

    private static InterceptorConfig ConfigFor(int port, int readIdleTimeoutSeconds = 1) => new()
    {
        ListenAddress = "127.0.0.1",
        ListenPort = port,
        ReadIdleTimeoutSeconds = readIdleTimeoutSeconds
    };

    private static async Task SendAsync(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var bytes = Encoding.ASCII.GetBytes(payload);
        await client.GetStream().WriteAsync(bytes);
        await client.GetStream().FlushAsync();
    }

    /// <summary>
    /// White-box wait so the regression test knows the second job is already in the
    /// bounded channel before shutdown starts.
    /// </summary>
    private static async Task WaitForQueuedCountAsync(PrintJobServer server, int expected, TimeSpan timeout)
    {
        var field = typeof(PrintJobServer).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var queue = Assert.IsAssignableFrom<Channel<PrintJob>>(field.GetValue(server));

        using var cts = new CancellationTokenSource(timeout);
        while (queue.Reader.Count < expected)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    /// <summary>
    /// Waits until a connection handler is tracked, so shutdown begins while that
    /// handler is still allowed to finish its read.
    /// </summary>
    private static async Task WaitForHandlerCountAsync(PrintJobServer server, int expected, TimeSpan timeout)
    {
        var field = typeof(PrintJobServer).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = Assert.IsAssignableFrom<List<Task>>(field.GetValue(server));

        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            lock (handlers)
            {
                if (handlers.Count(task => !task.IsCompleted) >= expected)
                {
                    return;
                }
            }

            await Task.Delay(10, cts.Token);
        }
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

    [Fact]
    public async Task StopAsyncDrainsQueuedJobsAfterCancellingInFlightWork()
    {
        var port = FreePort();
        var processor = new CancellableFirstProcessor();
        using var hostCts = new CancellationTokenSource();
        await using var server = new PrintJobServer(processor, ConfigFor(port), NullLog.Instance);
        await server.StartAsync(hostCts.Token);

        await SendAsync(port, "first receipt\n");
        await processor.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(processor.FirstToken.CanBeCanceled);

        await SendAsync(port, "second receipt\n");
        await WaitForQueuedCountAsync(server, 1, TimeSpan.FromSeconds(10));
        Assert.Equal(1, processor.CallCount);

        var stop = server.StopAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(20));

        // The in-flight call still saw application shutdown, but the queued job
        // was drained instead of being lost with the cancelled worker.
        Assert.True(processor.FirstToken.IsCancellationRequested);
        var drained = await processor.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("second receipt\n", Encoding.ASCII.GetString(drained.Data));
        Assert.Equal(2, processor.CallCount);
    }

    [Fact]
    public async Task StopAsyncLetsMidReadHandlerEnqueueItsJob()
    {
        var port = FreePort();
        var processor = new RecordingProcessor();
        using var hostCts = new CancellationTokenSource();
        // Keep the idle timeout comfortably above the time spent orchestrating
        // shutdown, so only EOF (not an idle discard) completes this job.
        await using var server = new PrintJobServer(processor, ConfigFor(port, 10), NullLog.Instance);
        await server.StartAsync(hostCts.Token);

        using var client = await ConnectAsync(port);
        const string firstPart = "mid-read ";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(firstPart));
        await client.GetStream().FlushAsync();
        await WaitForHandlerCountAsync(server, 1, TimeSpan.FromSeconds(10));

        // Shutdown starts while this handler is still waiting for the rest of
        // the job. It must not be cancelled until it has had a chance to enqueue.
        var stop = server.StopAsync();

        const string rest = "receipt\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(rest));
        await client.GetStream().FlushAsync();
        client.Close();

        await stop.WaitAsync(TimeSpan.FromSeconds(20));

        var job = await processor.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstPart + rest, Encoding.ASCII.GetString(job.Data));
    }
}
