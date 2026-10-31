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
    private readonly JobStore _jobStore;
    private readonly PrintSnapshotStore _snapshots;
    private readonly OfflineReceiptService _offline;
    private readonly OfflineQueue _offlineQueue;
    private readonly ILog _log;

    public PrintInterceptorHostedService(
        PrintJobServer server,
        JobStore jobStore,
        PrintSnapshotStore snapshots,
        OfflineReceiptService offline,
        OfflineQueue offlineQueue,
        ILog log)
    {
        _server = server;
        _jobStore = jobStore;
        _snapshots = snapshots;
        _offline = offline;
        _offlineQueue = offlineQueue;
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
        _log.Info("PrintInterceptor is ready.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Info("PrintInterceptor is stopping.");
        await _server.StopAsync();
    }
}
