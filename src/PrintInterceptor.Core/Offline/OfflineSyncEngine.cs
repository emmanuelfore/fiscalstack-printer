using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.State;

namespace PrintInterceptor.Core.Offline;

public sealed record OfflineSyncOutcome(
    int Synced,
    int Remaining,
    bool Online,
    string? Error,
    bool Paused = false);

/// <summary>
/// Syncs offline-signed receipts oldest first. Any rejection or ambiguous
/// failure stops the queue: later receipts carry the next counters and cannot
/// be submitted without breaking the ZIMRA sequence. A rejection also writes a
/// durable pause marker, so neither a restart nor the periodic loop can skip
/// the rejected receipt until an operator resumes the queue.
/// </summary>
public sealed class OfflineSyncEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FiscalStackClient _client;
    private readonly OfflineReceiptService _offline;
    private readonly OfflineQueue _queue;
    private readonly JobLedger _ledger;
    private readonly ILog _log;

    public OfflineSyncEngine(
        FiscalStackClient client,
        OfflineReceiptService offline,
        OfflineQueue queue,
        JobLedger ledger,
        ILog log)
    {
        _client = client;
        _offline = offline;
        _queue = queue;
        _ledger = ledger;
        _log = log;
    }

    public bool HasPending => _queue.HasPending;

    public int PendingCount => _queue.Count;

    public bool IsPaused => _queue.IsPaused;

    public string? PauseReason => _queue.CurrentPause?.Reason;

    /// <summary>
    /// Clears the pause marker. Only call this after the rejected receipt has
    /// been reconciled; later receipts resume with their signed counters.
    /// </summary>
    public void Resume() => _queue.Resume();

    /// <summary>
    /// Serialises issuance of a new offline receipt with the sync loop. Without
    /// this, the background sync could send a receipt before the pipeline has
    /// recorded it, and MarkSynced would then be overwritten by the offline mark.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfflineSyncOutcome> SyncNowAsync(CancellationToken cancellationToken)
    {
        if (_queue.CurrentPause is { } pause)
        {
            return new OfflineSyncOutcome(0, _queue.Count, false, pause.Reason, Paused: true);
        }

        if (!_queue.HasPending)
        {
            return new OfflineSyncOutcome(0, 0, true, null);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_queue.HasPending)
            {
                return new OfflineSyncOutcome(0, 0, true, null);
            }

            if (!await _client.IsReachableAsync(cancellationToken))
            {
                return new OfflineSyncOutcome(0, _queue.Count, false, null);
            }

            var synced = 0;
            while (true)
            {
                if (!_queue.TryPeek(out var item, out var peekError))
                {
                    if (peekError.Length > 0)
                    {
                        return new OfflineSyncOutcome(synced, _queue.Count, true, peekError);
                    }

                    break;
                }

                var label = $"{item.JobHash[..Math.Min(12, item.JobHash.Length)]} " +
                            $"(global {item.ReceiptGlobalNo}, counter {item.ReceiptCounter})";
                _log.Info($"Syncing offline receipt {label}...");

                var outcome = await _client.FiscaliseAsync(item.Request, cancellationToken);

                if (outcome.Status == FiscalizeOutcomeStatus.Success && outcome.Response is not null)
                {
                    _ledger.MarkSynced(item.JobHash, outcome.Response);
                    _offline.RecordOnlineSuccess(outcome.Response);
                    _queue.Remove(item);
                    synced++;
                    _log.Info($"Offline receipt {label} synced successfully.");
                    continue;
                }

                if (outcome.Status == FiscalizeOutcomeStatus.Rejected)
                {
                    var error = outcome.Error ?? "The server rejected the offline receipt.";
                    _queue.Pause(item, error);
                    _ledger.MarkFailure(item.JobHash, LedgerStatus.Rejected, error);
                    _queue.MoveToFailed(item, error);
                    _log.Error(
                        $"Offline receipt {label} was REJECTED: {error}. The queue is PAUSED; " +
                        "reconcile the receipt with FiscalStack, then resume from the tray menu.");
                    return new OfflineSyncOutcome(synced, _queue.Count, true, error, Paused: true);
                }

                var ambiguous = outcome.Error ?? "The sync outcome is unknown.";
                _queue.RecordAttempt(item, ambiguous);
                _ledger.MarkFailure(item.JobHash, LedgerStatus.AmbiguousFailure, ambiguous);
                _log.Error(
                    $"Offline receipt {label} sync is UNKNOWN: {ambiguous}. " +
                    "The queue is paused; reconcile with the server before retrying.");
                return new OfflineSyncOutcome(synced, _queue.Count, true, ambiguous);
            }

            return new OfflineSyncOutcome(synced, 0, true, null);
        }
        finally
        {
            _gate.Release();
        }
    }
}
