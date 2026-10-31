using Microsoft.Extensions.Hosting;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;

namespace PrintInterceptor;

/// <summary>
/// Periodically pushes offline-signed receipts to the API, oldest first. The
/// engine itself refuses to skip a failed receipt, so a rejection pauses the
/// queue instead of creating a gap in the ZIMRA counter chain.
/// </summary>
public sealed class OfflineSyncService : BackgroundService
{
    private const int SyncIntervalSeconds = 60;

    private readonly OfflineReceiptService _offline;
    private readonly OfflineSyncEngine _engine;
    private readonly FiscalStackClient _client;
    private readonly ILog _log;
    private bool _pauseReported;

    public OfflineSyncService(
        OfflineReceiptService offline,
        OfflineSyncEngine engine,
        FiscalStackClient client,
        ILog log)
    {
        _offline = offline;
        _engine = engine;
        _client = client;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_offline.Enabled && !_engine.HasPending && !_engine.IsPaused)
        {
            return;
        }

        if (!_offline.Enabled)
        {
            _log.Warn(
                $"Offline signing is disabled but {_engine.PendingCount} signed receipt(s) are still queued; " +
                "they will be synced.");
        }
        else if (!_offline.Ready)
        {
            _log.Warn($"Offline signing is enabled but not ready: {_offline.ConfigurationError}");
        }

        try
        {
            await _offline.RefreshStateAsync(_client, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not bootstrap the offline fiscal state: {ex.Message}");
        }

        if (_engine.HasPending || _engine.IsPaused)
        {
            await RunOnceAsync(stoppingToken);
        }

        var interval = TimeSpan.FromSeconds(SyncIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_engine.HasPending || _engine.IsPaused)
            {
                await RunOnceAsync(stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _engine.SyncNowAsync(cancellationToken);

            if (result.Paused)
            {
                if (!_pauseReported)
                {
                    _log.Error(
                        $"Offline sync is PAUSED: {result.Error}. {result.Remaining} receipt(s) stay queued " +
                        "until an operator reconciles the rejected receipt and resumes from the tray.");
                    _pauseReported = true;
                }

                return;
            }

            _pauseReported = false;
            if (result.Synced > 0)
            {
                _log.Info($"Offline sync completed: {result.Synced} receipt(s), {result.Remaining} remaining.");
            }
            else if (!result.Online && result.Remaining > 0)
            {
                _log.Warn($"Offline sync skipped: API unreachable, {result.Remaining} receipt(s) pending.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Offline sync failed", ex);
        }
    }
}
