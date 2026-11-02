using Microsoft.Extensions.Hosting;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Pipeline;

namespace PrintInterceptor;

/// <summary>
/// Runs the reconciliation pass for receipts whose fiscalisation outcome is unknown — a timeout
/// or a 5xx leaves the till unable to tell a missing sale from a printed one. One pass at startup,
/// then every reconcile.intervalMinutes; 0 leaves it to the tray's manual pass.
/// </summary>
public sealed class ReconcileService : BackgroundService
{
    private readonly PrintJobPipeline _pipeline;
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private bool _intervalDisabledReported;

    public ReconcileService(PrintJobPipeline pipeline, InterceptorConfig config, ILog log)
    {
        _pipeline = pipeline;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        while (true)
        {
            // Read on every turn so an edit to reconcile.intervalMinutes applies without a restart.
            var minutes = _config.Reconcile.IntervalMinutes;
            if (minutes <= 0)
            {
                // Nothing to poll, but keep a slow heartbeat so turning the interval back on in the
                // config takes effect by itself, and say so once rather than every hour.
                minutes = 60;
                if (!_intervalDisabledReported)
                {
                    _intervalDisabledReported = true;
                    _log.Info(
                        "reconcile.intervalMinutes is 0; reconciliation runs at startup and from the tray only.");
                }
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }

            if (_config.Reconcile.IntervalMinutes > 0)
            {
                await RunOnceAsync(stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var report = await _pipeline.ReconcileAsync(stoppingToken);

            if (report.Scanned > 0)
            {
                _log.Info($"Reconciliation: {report.Summary}");
            }
            else if (report.Error is { } error)
            {
                _log.Warn($"Reconciliation did not run: {error}");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Reconciliation pass failed", ex);
        }
    }
}
