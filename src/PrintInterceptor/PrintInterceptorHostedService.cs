using Microsoft.Extensions.Hosting;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

public sealed class PrintInterceptorHostedService : IHostedService
{
    private readonly PrintJobServer _server;
    private readonly WatchedPdfService _watchedPdf;
    private readonly JobStore _jobStore;
    private readonly PrintSnapshotStore _snapshots;
    private readonly OfflineReceiptService _offline;
    private readonly OfflineQueue _offlineQueue;
    private readonly FiscalStackClient _client;
    private readonly ILog _log;

    public PrintInterceptorHostedService(
        PrintJobServer server,
        WatchedPdfService watchedPdf,
        JobStore jobStore,
        PrintSnapshotStore snapshots,
        OfflineReceiptService offline,
        OfflineQueue offlineQueue,
        FiscalStackClient client,
        ILog log)
    {
        _server = server;
        _watchedPdf = watchedPdf;
        _jobStore = jobStore;
        _snapshots = snapshots;
        _offline = offline;
        _offlineQueue = offlineQueue;
        _client = client;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _jobStore.Prune();
        _snapshots.Prune();

        if (_offline.Enabled)
        {
            if (_offline.Ready)
            {
                _log.Info("Offline signing is enabled and the device key is loaded.");
            }
            else
            {
                _log.Warn($"Offline signing is enabled but not ready: {_offline.ConfigurationError}");
            }
        }

        if (_offlineQueue.HasPending)
        {
            _log.Warn($"{_offlineQueue.Count} offline-signed receipt(s) are waiting to sync.");
        }

        await _server.StartAsync(cancellationToken);
        await _watchedPdf.StartAsync(cancellationToken);

        // Keep the online fiscalise connection warm so the first receipt after the
        // till idles does not pay a cold TLS handshake before anything prints.
        _client.StartKeepWarm();

        _log.Info("PrintInterceptor is ready.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Info("PrintInterceptor is stopping.");

        // Stop dropping new files into the capture queue before the server drains it.
        await _watchedPdf.StopAsync();
        await _server.StopAsync();
    }
}
