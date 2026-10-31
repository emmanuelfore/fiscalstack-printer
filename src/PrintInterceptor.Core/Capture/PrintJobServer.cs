using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Capture;

/// <summary>
/// Raw TCP print listener. The Windows spooler pushes the job over the network
/// port and closes the connection; no file or folder is involved.
/// </summary>
public sealed class PrintJobServer : IAsyncDisposable
{
    private readonly IJobProcessor _processor;
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly Channel<PrintJob> _queue;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _workerTask;

    public PrintJobServer(IJobProcessor processor, InterceptorConfig config, ILog log)
    {
        _processor = processor;
        _config = config;
        _log = log;
        _queue = Channel.CreateBounded<PrintJob>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var address = IPAddress.Parse(_config.ListenAddress);
        _listener = new TcpListener(address, _config.ListenPort);
        _listener.Start(32);

        _acceptTask = AcceptLoopAsync(_cts.Token);
        _workerTask = WorkerLoopAsync(_cts.Token);

        _log.Info($"Print listener started on {address}:{_config.ListenPort}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Accepting a print connection failed", ex);
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            client.NoDelay = true;
            using (client)
            {
                var data = await ReadJobAsync(client.GetStream(), cancellationToken);
                if (data.Length == 0)
                {
                    _log.Warn($"Ignoring empty print job from {remote}");
                    return;
                }

                if (data.Length > _config.MaxJobBytes)
                {
                    _log.Error($"Rejecting print job from {remote}: {data.Length} bytes exceeds maxJobBytes.");
                    return;
                }

                _log.Info($"Captured {data.Length} bytes from {remote}");
                await _queue.Writer.WriteAsync(new PrintJob(data, remote, DateTimeOffset.Now), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
        catch (Exception ex)
        {
            _log.Error($"Reading a print job from {remote} failed", ex);
        }
    }

    private async Task<byte[]> ReadJobAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var firstRead = true;

        while (true)
        {
            // The spooler may pause mid-job, so allow a short idle window before
            // treating the socket as finished.
            var timeout = firstRead ? 15 : _config.ReadIdleTimeoutSeconds;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(TimeSpan.FromSeconds(timeout));

            int read;
            try
            {
                read = await stream.ReadAsync(chunk.AsMemory(), idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // Idle: the peer finished sending.
            }

            if (read == 0)
            {
                break; // Clean EOF.
            }

            firstRead = false;
            if (buffer.Length + read > _config.MaxJobBytes)
            {
                throw new InvalidOperationException($"Print job exceeds maxJobBytes ({_config.MaxJobBytes}).");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _processor.ProcessAsync(job, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("Unhandled error while processing a print job", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _queue.Writer.TryComplete();

        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { /* ignored */ }
        }

        if (_workerTask is not null)
        {
            try { await _workerTask; } catch { /* ignored */ }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _log.Info("Print listener stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
