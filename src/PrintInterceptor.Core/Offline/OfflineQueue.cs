using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.State;

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
    private readonly string _recoveryDirectory;
    private readonly string _pausePath;
    private readonly ILog _log;
    private OfflinePause? _pause;
    private long _sequence;

    public OfflineQueue(string stateDirectory, ILog log)
    {
        _log = log;
        _directory = Path.Combine(stateDirectory, "offline-queue");
        _failedDirectory = Path.Combine(stateDirectory, "offline-failed");
        _recoveryDirectory = Path.Combine(stateDirectory, "offline-recovery");
        _pausePath = Path.Combine(stateDirectory, "offline-sync-pause.json");
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_failedDirectory);
        Directory.CreateDirectory(_recoveryDirectory);
        _pause = LoadPause();

        // Recovery is deliberately not run here. A signed receipt must only be
        // re-admitted once the caller has supplied the committed chain state;
        // otherwise a record written just before a failed chain commit would be
        // submitted with a counter that was never spent.
    }

    /// <summary>
    /// Supplies the durable chain state used to decide whether a recovery record
    /// represents a committed receipt or a signing attempt that never committed.
    /// Set once during pipeline construction before RecoverPending is called.
    /// </summary>
    public Func<FiscalChainSnapshot?>? ChainProvider { get; set; }

    public string DirectoryPath => _directory;

    /// <summary>Where signed receipts go when the queue write itself fails.</summary>
    public string RecoveryDirectoryPath => _recoveryDirectory;

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
        // Read the chain before taking the queue lock. Signing holds the chain lock
        // while it asks us to protect a receipt, so the reverse order would deadlock.
        var chain = ChainProvider?.Invoke();
        lock (_gate)
        {
            // Any earlier receipt that was signed but could not be queued owns the next
            // counters, so it goes back into the FIFO before this one.
            RecoverPendingLocked(chain);
            return TryEnqueueLocked(item, out error);
        }
    }

    /// <summary>
    /// Writes a signed receipt to durable storage while the chain lock is held, before the
    /// counter is persisted. The record is moved into the FIFO after the chain commits; a
    /// crash in between is recovered by comparing its number with the committed chain.
    /// </summary>
    public bool TrySaveRecovery(OfflineQueueItem item, out string path, out string error)
    {
        lock (_gate)
        {
            path = "";
            try
            {
                Directory.CreateDirectory(_recoveryDirectory);
                path = Path.Combine(
                    _recoveryDirectory,
                    $"{DateTimeOffset.Now:yyyyMMddHHmmssfff}-{Short(item.JobHash)}-{Guid.NewGuid():N}.json");
                WriteAtomic(path, item);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                path = "";
                error = ex.Message;
                _log.Error("Could not protect the signed offline receipt before committing its counter", ex);
                return false;
            }
        }
    }

    /// <summary>Promotes a committed recovery record into the FIFO in one atomic move.</summary>
    public bool PromoteRecovery(string path, out string error)
    {
        lock (_gate)
        {
            error = "";
            try
            {
                if (!File.Exists(path))
                {
                    error = "The protected signed receipt disappeared before it could be queued.";
                    return false;
                }

                var item = JsonSerializer.Deserialize<OfflineQueueItem>(File.ReadAllText(path), JsonOptions)
                           ?? throw new InvalidOperationException("recovery file is empty");
                if (QueueHasLocked(item, out var corrupt))
                {
                    TryDelete(path);
                    return true;
                }

                if (corrupt)
                {
                    error = "An existing queue file could not be read, so the recovery record was kept.";
                    return false;
                }

                File.Move(path, QueuePath(item), overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _log.Error($"Could not queue the protected offline receipt {Path.GetFileName(path)}", ex);
                return false;
            }
        }
    }

    /// <summary>Discards a recovery record whose chain counter was never committed.</summary>
    public void DiscardRecovery(string path)
    {
        lock (_gate)
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Re-admits signed receipts whose enqueue or chain promotion was interrupted. A record
    /// whose global number is ahead of the chain belongs to a signing attempt that never
    /// committed and is discarded; a committed record is moved into the FIFO. A successful
    /// recovery is one File.Move, so a crash cannot leave two submissions of the same receipt.
    /// </summary>
    public int RecoverPending()
    {
        // Read the chain before taking the queue lock to keep lock order consistent
        // with the signing path (chain -> queue).
        var chain = ChainProvider?.Invoke();
        lock (_gate)
        {
            return RecoverPendingLocked(chain);
        }
    }

    private int RecoverPendingLocked(FiscalChainSnapshot? chain)
    {
        if (chain is null)
        {
            _log.Error("The offline fiscal chain is unavailable; signed receipts stay in recovery until it can be read.");
            return 0;
        }

        var recovered = 0;
        if (!Directory.Exists(_recoveryDirectory))
        {
            return recovered;
        }

        foreach (var path in Directory.EnumerateFiles(_recoveryDirectory, "*.json")
                     .OrderBy(name => name, StringComparer.Ordinal).ToList())
        {
            OfflineQueueItem item;
            try
            {
                item = JsonSerializer.Deserialize<OfflineQueueItem>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidOperationException("file is empty");
            }
            catch (Exception ex)
            {
                _log.Error($"Could not read the recovered offline receipt {Path.GetFileName(path)}", ex);
                continue;
            }

            if (chain is not null && item.ReceiptGlobalNo > chain.ReceiptGlobalNo)
            {
                TryDelete(path);
                _log.Warn(
                    $"Discarded an uncommitted offline signing attempt for {Short(item.JobHash)} " +
                    $"(global {item.ReceiptGlobalNo}, chain {chain.ReceiptGlobalNo}); no counter was spent.");
                continue;
            }

            try
            {
                if (QueueHasLocked(item, out var corrupt))
                {
                    if (!corrupt)
                    {
                        TryDelete(path);
                    }
                    else
                    {
                        _log.Error(
                            $"A queue file could not be read while recovering {Short(item.JobHash)}; " +
                            "the recovery record was kept.");
                    }
                    continue;
                }

                File.Move(path, QueuePath(item), overwrite: false);
                recovered++;
            }
            catch (Exception ex)
            {
                _log.Error($"Could not re-queue the recovered offline receipt {Path.GetFileName(path)}", ex);
            }
        }

        if (recovered > 0)
        {
            _log.Warn($"{recovered} signed offline receipt(s) were recovered into the sync queue.");
        }

        return recovered;
    }

    private bool QueueHasLocked(OfflineQueueItem item, out bool corrupt)
    {
        corrupt = false;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            OfflineQueueItem existing;
            try
            {
                existing = JsonSerializer.Deserialize<OfflineQueueItem>(File.ReadAllText(path), JsonOptions)
                           ?? throw new InvalidOperationException("queue file is empty");
            }
            catch
            {
                corrupt = true;
                return false;
            }

            if (string.Equals(existing.JobHash, item.JobHash, StringComparison.Ordinal) &&
                existing.ReceiptGlobalNo == item.ReceiptGlobalNo)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEnqueueLocked(OfflineQueueItem item, out string error)
    {
        try
        {
            var path = QueuePath(item);
            WriteAtomic(path, item);
            item.FilePath = path;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not queue the offline receipt", ex);
            WriteRecoveryLocked(item);
            error = ex.Message;
            return false;
        }
    }

    private string QueuePath(OfflineQueueItem item)
    {
        var stamp = item.CreatedAt == default ? DateTimeOffset.Now : item.CreatedAt;
        var sequence = Interlocked.Increment(ref _sequence).ToString("D6");
        return Path.Combine(
            _directory,
            $"{stamp:yyyyMMddHHmmssfff}-{sequence}-{Short(item.JobHash)}.json");
    }

    /// <summary>
    /// The chain has already advanced and the receipt is signed: losing it here would skip a
    /// ZIMRA counter permanently. Keep the exact signed body so the next enqueue or sync pass
    /// can put it back without re-signing it.
    /// </summary>
    private void WriteRecoveryLocked(OfflineQueueItem item)
    {
        try
        {
            Directory.CreateDirectory(_recoveryDirectory);
            var path = Path.Combine(
                _recoveryDirectory,
                $"{DateTimeOffset.Now:yyyyMMddHHmmssfff}-{Short(item.JobHash)}-{Guid.NewGuid():N}.json");
            WriteAtomic(path, item);
            _log.Warn(
                $"Signed offline receipt {Short(item.JobHash)} was kept for recovery at {path}; " +
                "no counter was skipped and it will be re-queued before the next sync.");
        }
        catch (Exception ex)
        {
            _log.Error("Could not save the signed offline receipt for recovery", ex);
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked file is recovered on the next pass; never fail a queue
            // operation just because cleanup could not run.
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
