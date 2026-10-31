using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;

namespace PrintInterceptor.Core.Offline;

/// <summary>
/// Durable marker for a queue that must not be synced until an operator has
/// reconciled a rejected receipt with FiscalStack.
/// </summary>
public sealed class OfflinePause
{
    public string Reason { get; set; } = "";
    public string JobHash { get; set; } = "";
    public DateTimeOffset PausedAt { get; set; }
}

public sealed class OfflineQueueItem
{
    public string JobHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public FiscalizeRequest Request { get; set; } = new();
    public string Signature { get; set; } = "";
    public string SignedHash { get; set; } = "";
    public string VerificationCode { get; set; } = "";
    public string QrCodeUrl { get; set; } = "";
    public string ReceiptDate { get; set; } = "";
    public int ReceiptCounter { get; set; }
    public int ReceiptGlobalNo { get; set; }
    public int FiscalDayNo { get; set; }
    public decimal ReceiptTotal { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore]
    public string? FilePath { get; set; }
}

/// <summary>
/// Durable FIFO of offline-signed receipts waiting for the API. Order matters:
/// the server only accepts the exact next ZIMRA counter, so receipts must sync
/// oldest first and a failure stops the queue instead of skipping ahead.
/// </summary>
public sealed class OfflineQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _failedDirectory;
    private readonly string _pausePath;
    private readonly ILog _log;
    private OfflinePause? _pause;
    private long _sequence;

    public OfflineQueue(string stateDirectory, ILog log)
    {
        _log = log;
        _directory = Path.Combine(stateDirectory, "offline-queue");
        _failedDirectory = Path.Combine(stateDirectory, "offline-failed");
        _pausePath = Path.Combine(stateDirectory, "offline-sync-pause.json");
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_failedDirectory);
        _pause = LoadPause();
    }

    public string DirectoryPath => _directory;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pause is not null;
            }
        }
    }

    public OfflinePause? CurrentPause
    {
        get
        {
            lock (_gate)
            {
                return _pause;
            }
        }
    }

    /// <summary>
    /// Stops the queue after a definite rejection. Later receipts were signed
    /// with counters past the rejected one, so submitting them would break the
    /// ZIMRA sequence. The marker survives restarts until an operator resumes.
    /// </summary>
    public void Pause(OfflineQueueItem item, string error)
    {
        lock (_gate)
        {
            var pause = new OfflinePause
            {
                Reason = error,
                JobHash = item.JobHash,
                PausedAt = DateTimeOffset.Now
            };

            _pause = pause;
            try
            {
                WriteAtomic(_pausePath, pause);
                _log.Error($"Offline sync paused at receipt {Short(item.JobHash)}: {error}");
            }
            catch (Exception ex)
            {
                _log.Error(
                    "Could not persist the offline-sync pause; this run stays paused but a restart will resume syncing",
                    ex);
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _pause = null;
            try
            {
                if (File.Exists(_pausePath))
                {
                    File.Delete(_pausePath);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not delete {_pausePath}; the queue will pause again after a restart: {ex.Message}");
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return Directory.EnumerateFiles(_directory, "*.json").Any();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Directory.EnumerateFiles(_directory, "*.json").Count();
            }
        }
    }

    public bool TryEnqueue(OfflineQueueItem item, out string error)
    {
        lock (_gate)
        {
            try
            {
                var stamp = item.CreatedAt == default ? DateTimeOffset.Now : item.CreatedAt;
                var sequence = Interlocked.Increment(ref _sequence).ToString("D6");
                var path = Path.Combine(
                    _directory,
                    $"{stamp:yyyyMMddHHmmssfff}-{sequence}-{Short(item.JobHash)}.json");

                WriteAtomic(path, item);
                item.FilePath = path;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Could not queue the offline receipt", ex);
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Returns the oldest receipt. A corrupt queue file fails closed: the caller
    /// must reconcile it rather than skipping it and breaking the counter chain.
    /// </summary>
    public bool TryPeek(out OfflineQueueItem item, out string error)
    {
        lock (_gate)
        {
            item = null!;
            var path = Directory.EnumerateFiles(_directory, "*.json").OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
            if (path is null)
            {
                error = "";
                return false;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<OfflineQueueItem>(File.ReadAllText(path), JsonOptions)
                             ?? throw new InvalidOperationException("file is empty");
                parsed.FilePath = path;
                item = parsed;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Offline queue file {path} is unreadable", ex);
                error = $"Offline queue file {Path.GetFileName(path)} is unreadable: {ex.Message}";
                return false;
            }
        }
    }

    public void Remove(OfflineQueueItem item)
    {
        lock (_gate)
        {
            if (item.FilePath is not null && File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }
    }

    public void RecordAttempt(OfflineQueueItem item, string? error)
    {
        lock (_gate)
        {
            item.Attempts++;
            item.LastError = error;
            if (item.FilePath is null || !File.Exists(item.FilePath))
            {
                return;
            }

            try
            {
                WriteAtomic(item.FilePath, item);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not update the offline queue entry: {ex.Message}");
            }
        }
    }

    public void MoveToFailed(OfflineQueueItem item, string error)
    {
        lock (_gate)
        {
            item.LastError = error;
            if (item.FilePath is null || !File.Exists(item.FilePath))
            {
                return;
            }

            var destination = Path.Combine(_failedDirectory, Path.GetFileName(item.FilePath));
            try
            {
                WriteAtomic(destination, item);
                File.Delete(item.FilePath);
                _log.Warn($"Offline receipt moved to {destination}: {error}");
            }
            catch (Exception ex)
            {
                _log.Error($"Could not move the rejected offline receipt to {_failedDirectory}", ex);
            }
        }
    }

    private OfflinePause? LoadPause()
    {
        if (!File.Exists(_pausePath))
        {
            return null;
        }

        try
        {
            var pause = JsonSerializer.Deserialize<OfflinePause>(File.ReadAllText(_pausePath), JsonOptions)
                         ?? throw new InvalidOperationException("file is empty");
            _log.Warn(
                $"Offline sync is paused since {pause.PausedAt:u}: {pause.Reason}. " +
                "Reconcile the receipt, then resume from the tray menu.");
            return pause;
        }
        catch (Exception ex)
        {
            // Fail closed: without a readable marker we cannot know which
            // counters were already rejected, so refuse to sync until an
            // operator resumes deliberately.
            _log.Error($"Offline sync pause marker {_pausePath} is unreadable", ex);
            return new OfflinePause
            {
                Reason = $"The offline-sync pause marker is unreadable: {ex.Message}",
                PausedAt = DateTimeOffset.Now
            };
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string Short(string value) =>
        value.Length <= 12 ? value : value[..12];
}
