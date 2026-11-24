using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Capture;

/// <summary>
/// Watches a folder for PDF invoices saved by the POS. The bytes are handed to
/// <see cref="PrintJobServer.EnqueueAsync"/>, so a dropped file travels through the
/// same bounded queue, pause gate, shutdown drain and duplicate-safe pipeline as a
/// job received on the TCP print port. Files are only claimed once their size and
/// timestamp stop changing, which avoids fiscalising a half-written PDF.
/// </summary>
public sealed class WatchedPdfService
{
    private const int ScanIntervalMilliseconds = 500;
    private const string ProcessedFolder = "processed";
    private const string RejectedFolder = "rejected";

    private readonly InterceptorConfig _config;
    private readonly PrintJobServer _server;
    private readonly ILog _log;
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed record Candidate(long Length, DateTime LastWriteUtc, DateTimeOffset FirstSeen);

    public WatchedPdfService(InterceptorConfig config, PrintJobServer server, ILog log)
    {
        _config = config;
        _server = server;
        _log = log;
    }

    public bool Enabled =>
        _config.Capture.Enabled && !string.IsNullOrWhiteSpace(_config.Capture.PdfWatchDirectory);

    public string WatchDirectory =>
        Path.GetFullPath((_config.Capture.PdfWatchDirectory ?? "").Trim());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            _log.Info("PDF drop folder capture is disabled.");
            return Task.CompletedTask;
        }

        var directory = WatchDirectory;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, ProcessedFolder));
        Directory.CreateDirectory(Path.Combine(directory, RejectedFolder));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        _log.Info(
            $"Watching {directory} for saved PDF invoices. Processed files move to '{ProcessedFolder}', " +
            $"rejected files to '{RejectedFolder}'.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _log.Error("The PDF drop folder watcher stopped with an error", ex);
            }
        }

        cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ScanIntervalMilliseconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("A PDF drop folder scan failed", ex);
            }

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        var directory = WatchDirectory;
        var files = Directory.GetFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly);
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var stale in _candidates.Keys.Where(path => !present.Contains(path)).ToList())
            {
                _candidates.Remove(stale);
            }
        }

        var now = DateTimeOffset.Now;
        var stability = TimeSpan.FromMilliseconds(_config.Capture.StabilityMilliseconds);

        foreach (var path in files.OrderBy(path => File.GetLastWriteTimeUtc(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                info.Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bool shouldProcess;
            lock (_gate)
            {
                var known = _candidates.TryGetValue(path, out var candidate);
                var unchanged = known &&
                                candidate!.Length == info.Length &&
                                candidate.LastWriteUtc == info.LastWriteTimeUtc;

                // Keep the first-seen time while a file is unchanged. Restarting it on
                // every scan would never reach a stability window longer than one scan.
                if (!unchanged)
                {
                    _candidates[path] = new Candidate(info.Length, info.LastWriteTimeUtc, now);
                }

                shouldProcess = unchanged && now - candidate!.FirstSeen >= stability;
            }

            if (!shouldProcess)
            {
                continue;
            }

            lock (_gate)
            {
                _candidates.Remove(path);
            }

            var claimed = await ProcessFileAsync(path, cancellationToken);
            if (!claimed)
            {
                lock (_gate)
                {
                    _candidates[path] = new Candidate(info.Length, info.LastWriteTimeUtc, DateTimeOffset.Now);
                }
            }
        }
    }

    /// <summary>Returns true when the file was claimed, even if it was then rejected.</summary>
    private async Task<bool> ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        var directory = WatchDirectory;
        var claimed = Claim(path, Path.Combine(directory, ProcessedFolder));
        if (claimed is null)
        {
            return false;
        }

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(
                       claimed,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length == 0)
                {
                    Reject(claimed, Path.Combine(directory, RejectedFolder), "empty");
                    return true;
                }

                if (stream.Length > _config.MaxJobBytes)
                {
                    Reject(claimed, Path.Combine(directory, RejectedFolder), "too-large");
                    return true;
                }

                bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0)
                    {
                        break;
                    }

                    read += count;
                }

                if (read != bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }
            }

            if (JobDecoder.DetectFormat(bytes) != JobFormat.Pdf)
            {
                Reject(claimed, Path.Combine(directory, RejectedFolder), "not-pdf");
                return true;
            }

            var source = "file://" + claimed.Replace('\\', '/');
            await _server.EnqueueAsync(new PrintJob(bytes, source, DateTimeOffset.Now), cancellationToken);
            _log.Info($"Queued PDF drop '{Path.GetFileName(claimed)}' ({bytes.Length} bytes) from {claimed}.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The claimed file stays in processed for a deliberate re-drive.
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"Could not queue PDF drop '{claimed}'; it was kept in '{ProcessedFolder}' for manual recovery",
                ex);
            return true;
        }
    }

    private static string? Claim(string path, string processedDirectory)
    {
        var destination = UniquePath(processedDirectory, Path.GetFileName(path));
        try
        {
            File.Move(path, destination);
            return destination;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Reject(string path, string rejectedDirectory, string reason)
    {
        var name = $"{reason}-{Path.GetFileName(path)}";
        var destination = UniquePath(rejectedDirectory, name);
        try
        {
            File.Move(path, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The rejected file remains in processed; it is never processed twice.
        }
    }

    private static string UniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var destination = Path.Combine(directory, $"{stamp}-{fileName}");

        for (var suffix = 2; File.Exists(destination); suffix++)
        {
            destination = Path.Combine(directory, $"{stamp}-{baseName}-{suffix}{extension}");
        }

        return destination;
    }
}
