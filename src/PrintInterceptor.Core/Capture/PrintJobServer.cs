using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Capture;

/// <summary>
/// Raw TCP print listener. The Windows spooler pushes the job over the network
/// port and closes the connection; no file or folder is involved.
/// A sender may instead frame a job as "FJOB" + 4-byte big-endian length + payload.
/// Either a clean EOF or a complete frame is required before a job is queued; an
/// idle connection with a partial body is discarded, never fiscalised.
/// </summary>
public sealed class PrintJobServer : IAsyncDisposable
{
    private const int MaxConcurrentClients = 8;
    private const int FirstReadTimeoutSeconds = 10;
    private const int MaxAcceptBackoffMilliseconds = 5000;
    private const int FrameHeaderLength = 8;
    private const int MinimumDrainBudgetSeconds = 30;
    private const int MinimumHandlerDrainSeconds = 15;

    private static readonly byte[] FrameMagic = { (byte)'F', (byte)'J', (byte)'O', (byte)'B' };

    private readonly IJobProcessor _processor;
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly Channel<PrintJob> _queue;
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients);
    private readonly List<Task> _handlers = new();
    private readonly object _handlersGate = new();
    private readonly object _stopGate = new();

    private TaskCompletionSource _resumeGate = CompletedGate();
    private int _processingPaused;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _handlerCts;
    private CancellationTokenSource? _workerCts;
    private CancellationTokenSource? _drainCts;
    private CancellationTokenRegistration _externalRegistration;
    private Task? _stopTask;
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

    /// <summary>
    /// True while the operator has paused fiscalisation. Connections are still accepted and
    /// their bytes captured — the spooler must never see a refused print — but jobs wait in
    /// the queue instead of being processed until <see cref="ResumeProcessing"/>.
    /// </summary>
    public bool IsProcessingPaused => Volatile.Read(ref _processingPaused) == 1;

    /// <summary>Raised when the operator pauses or resumes processing, so the tray and dashboard agree.</summary>
    public event EventHandler? ProcessingPauseChanged;

    public void PauseProcessing()
    {
        if (Interlocked.Exchange(ref _processingPaused, 1) == 0)
        {
            _resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _log.Warn("Capture processing paused by the operator. Jobs are still captured and queue until resumed.");
            ProcessingPauseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResumeProcessing()
    {
        if (Interlocked.Exchange(ref _processingPaused, 0) == 1)
        {
            _resumeGate.TrySetResult();
            _log.Info("Capture processing resumed. Queued jobs will now be fiscalised.");
            ProcessingPauseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    /// <summary>
    /// Re-checks the flag after every wake so a pause/resume that races the gate swap can
    /// never leave the worker waiting on a gate nobody will complete.
    /// </summary>
    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _processingPaused) == 1)
        {
            await _resumeGate.Task.WaitAsync(cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stopTask is not null)
        {
            throw new ObjectDisposedException(nameof(PrintJobServer));
        }

        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        // _cts follows the host's lifetime and cancels accepting plus an API call
        // already in flight. _handlerCts is deliberately independent: a handler
        // that has captured bytes must be allowed to finish writing them even
        // after host cancellation. _workerCts only ends after the queue is drained.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handlerCts = new CancellationTokenSource();
        _workerCts = new CancellationTokenSource();
        if (cancellationToken.CanBeCanceled)
        {
            _externalRegistration = cancellationToken.Register(
                static state => ((PrintJobServer)state!).RequestStop(), this);
        }

        // Register invokes the callback synchronously when the token is already
        // cancelled; do not start a listener that shutdown just finished closing.
        if (cancellationToken.IsCancellationRequested)
        {
            return StopAsync();
        }

        var address = IPAddress.Parse(_config.ListenAddress);
        _listener = new TcpListener(address, _config.ListenPort);
        _listener.Start(32);

        _acceptTask = AcceptLoopAsync(_cts.Token, _handlerCts.Token);
        _workerTask = WorkerLoopAsync(_workerCts.Token);

        _log.Info($"Print listener started on {address}:{_config.ListenPort}");
        return Task.CompletedTask;
    }

    private void RequestStop()
    {
        // A host may cancel its token without calling StopAsync. Start the same
        // orderly shutdown, and observe the task if nobody else awaits it.
        _ = StopAsync().ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken, CancellationToken handlerToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                failures = 0;
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
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                failures++;
                // Bounded exponential backoff: a broken listener must not spin the CPU
                // or flood the log, but a transient failure is retried quickly.
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(100 * (1 << Math.Min(failures - 1, 6)), MaxAcceptBackoffMilliseconds));
                _log.Error($"Accepting a print connection failed (attempt {failures}); retrying in {delay.TotalSeconds:0.#}s", ex);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // Backpressure at the socket rather than an unbounded pile of readers.
            try
            {
                await _clientSlots.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                break;
            }

            // Host cancellation stops the listener, not a job this client has
            // already started sending.
            TrackHandler(HandleClientAsync(client, handlerToken));
        }
    }

    private void TrackHandler(Task handler)
    {
        lock (_handlersGate)
        {
            _handlers.RemoveAll(task => task.IsCompleted);
            _handlers.Add(handler);
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
                var data = await ReadJobAsync(client.GetStream(), remote, cancellationToken);
                if (data is null)
                {
                    // Idle timeout / short frame: already logged, nothing to queue.
                    return;
                }

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
        finally
        {
            _clientSlots.Release();
        }
    }

    /// <summary>
    /// Returns null when the sender went idle without closing the stream (or sent a
    /// short frame), an empty array on a clean EOF with no bytes, and the job bytes
    /// on a clean EOF or a complete "FJOB" frame.
    /// </summary>
    private async Task<byte[]?> ReadJobAsync(NetworkStream stream, string remote, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var firstRead = true;
        long frameLength = -1;

        while (true)
        {
            // A spooler may pause mid-job, but a connection that never sends its first
            // byte should not hold a client slot for the full idle timeout.
            var timeout = firstRead ? FirstReadTimeoutSeconds : _config.ReadIdleTimeoutSeconds;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(TimeSpan.FromSeconds(timeout));

            int read;
            try
            {
                read = await stream.ReadAsync(chunk.AsMemory(), idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DiscardIncomplete(remote, buffer.Length, "idle timeout");
                return null;
            }

            if (read == 0)
            {
                if (frameLength >= 0 && buffer.Length - FrameHeaderLength != frameLength)
                {
                    DiscardIncomplete(remote, buffer.Length - FrameHeaderLength, "short frame");
                    return null;
                }

                return frameLength >= 0 ? FramePayload(buffer) : buffer.ToArray();
            }

            firstRead = false;
            var limit = frameLength >= 0 ? frameLength + FrameHeaderLength : _config.MaxJobBytes;
            if (buffer.Length + read > limit)
            {
                throw new InvalidOperationException($"Print job exceeds maxJobBytes ({_config.MaxJobBytes}).");
            }

            buffer.Write(chunk, 0, read);

            if (frameLength < 0 && buffer.Length >= FrameHeaderLength)
            {
                frameLength = TryReadFrameLength(buffer.GetBuffer());
                if (frameLength > 0 && buffer.Length >= frameLength + FrameHeaderLength)
                {
                    return FramePayload(buffer);
                }
            }
        }
    }

    private long TryReadFrameLength(byte[] head)
    {
        if (!head.AsSpan(0, FrameMagic.Length).SequenceEqual(FrameMagic))
        {
            return -1;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(FrameMagic.Length, 4));
        if (length <= 0 || length > _config.MaxJobBytes)
        {
            throw new InvalidOperationException("Framed print job declares an invalid length.");
        }

        return length;
    }

    private static byte[] FramePayload(MemoryStream buffer)
    {
        var payload = new byte[(int)(buffer.Length - FrameHeaderLength)];
        buffer.GetBuffer().AsSpan(FrameHeaderLength, payload.Length).CopyTo(payload);
        return payload;
    }

    private void DiscardIncomplete(string remote, long bytes, string reason)
    {
        _log.Warn($"Discarding incomplete print job from {remote}: {bytes} bytes, {reason}; " +
                  "no clean EOF or complete frame was received.");
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                // An operator pause holds jobs in the queue itself: counting what is
                // waiting stays honest, backpressure reaches the spooler, and shutdown
                // resumes first so the drain below is never stuck behind a pause.
                await WaitWhilePausedAsync(cancellationToken);

                while (_queue.Reader.TryRead(out var job))
                {
                    // A job that was already in flight when shutdown started keeps the
                    // host token, so an API call can still be cancelled. Jobs dequeued
                    // during the drain get a bounded token instead and still fiscalise.
                    var draining = _cts?.IsCancellationRequested == true;
                    var processToken = draining ? EnsureDrainCts().Token : _cts!.Token;

                    try
                    {
                        await _processor.ProcessAsync(job, processToken);
                    }
                    catch (OperationCanceledException) when (draining && processToken.IsCancellationRequested)
                    {
                        _log.Warn("Shutdown drain timed out; some queued print jobs may remain unprocessed.");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Either the in-flight API call was cancelled by application
                        // shutdown or the job cancelled itself. Keep draining the queue.
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Unhandled error while processing a print job", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only raised after the writer completed and the queue was drained.
        }
    }

    private CancellationTokenSource EnsureDrainCts()
    {
        lock (_stopGate)
        {
            if (_drainCts is null)
            {
                _drainCts = new CancellationTokenSource();
                var budget = DrainBudgetSeconds();
                _drainCts.CancelAfter(TimeSpan.FromSeconds(budget));
            }

            return _drainCts;
        }
    }

    private int DrainBudgetSeconds() => Math.Max(MinimumDrainBudgetSeconds, _config.ApiTimeoutSeconds + 10);

    /// <summary>
    /// Long enough for a handler that was mid-read at shutdown to see EOF (or its
    /// normal idle timeout) and enqueue the bytes it already captured.
    /// </summary>
    private TimeSpan HandlerDrainTimeout() =>
        TimeSpan.FromSeconds(Math.Max(MinimumHandlerDrainSeconds,
            Math.Max(FirstReadTimeoutSeconds, _config.ReadIdleTimeoutSeconds) + 5));

    private async Task<(bool TimedOut, Task[] Pending)> DrainHandlersAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_handlersGate)
            {
                _handlers.RemoveAll(task => task.IsCompleted);
                pending = _handlers.Where(task => !task.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return (false, Array.Empty<Task>());
            }

            var all = Task.WhenAll(pending);
            var finished = await Task.WhenAny(all, Task.Delay(HandlerDrainTimeout()));
            if (finished != all)
            {
                return (true, pending);
            }

            try
            {
                await all;
            }
            catch (Exception ex)
            {
                _log.Error("A print connection handler failed while stopping", ex);
            }
        }
    }

    private static void ObserveFaulted(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.IsCompleted)
            {
                _ = task.Exception;
            }
            else
            {
                _ = task.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_stopGate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        _ = RunStopAsync(completion);
        return completion.Task;
    }

    private async Task RunStopAsync(TaskCompletionSource completion)
    {
        try
        {
            // A paused capture must not hold the shutdown drain: whatever was
            // queued while paused is processed like any other pending job.
            ResumeProcessing();

            // 1. Stop accepting new work; application cancellation still reaches
            //    an API call that is already in flight.
            _externalRegistration.Unregister();
            _cts?.Cancel();
            _listener?.Stop();

            if (_acceptTask is not null)
            {
                try { await _acceptTask; } catch { /* ignored */ }
            }

            // 2. Give handlers that were mid-read a bounded chance to see EOF and
            //    enqueue the bytes they already captured. _handlerCts is still live.
            var (handlerTimedOut, stuckHandlers) = await DrainHandlersAsync();
            if (handlerTimedOut)
            {
                _log.Warn("Timed out waiting for print connection handlers; cancelling them.");
                _handlerCts?.Cancel();
                ObserveFaulted(stuckHandlers);
                await Task.WhenAny(Task.WhenAll(stuckHandlers), Task.Delay(TimeSpan.FromSeconds(2)));
            }

            // 3. Every surviving handler has finished writing; now it is safe to
            //    close the channel and let the worker drain the queued jobs.
            _queue.Writer.TryComplete();

            var workerTimedOut = false;
            if (_workerTask is not null)
            {
                var budget = TimeSpan.FromSeconds(DrainBudgetSeconds() + 5);
                var finished = await Task.WhenAny(_workerTask, Task.Delay(budget));
                if (finished == _workerTask)
                {
                    try { await _workerTask; } catch { /* ignored */ }
                }
                else
                {
                    // A processor that ignores its token must not hang shutdown.
                    workerTimedOut = true;
                    _workerCts?.Cancel();
                    _log.Warn("Timed out waiting for the print queue to drain; the worker was abandoned.");
                    ObserveFaulted(new[] { _workerTask });
                }
            }

            _handlerCts?.Cancel();
            _workerCts?.Cancel();

            // Leave tokens and the slot semaphore alive for any task that was
            // abandoned after a timeout; it may still read or release them.
            if (!workerTimedOut)
            {
                _drainCts?.Dispose();
                _cts?.Dispose();
                _cts = null;
                _workerCts?.Dispose();
                _workerCts = null;
            }

            if (!handlerTimedOut)
            {
                _handlerCts?.Dispose();
                _handlerCts = null;
                _clientSlots.Dispose();
            }

            _listener = null;
            _log.Info("Print listener stopped.");
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
