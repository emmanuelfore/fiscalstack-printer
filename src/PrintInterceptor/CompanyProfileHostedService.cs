using Microsoft.Extensions.Hosting;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;

namespace PrintInterceptor;

/// <summary>
/// Refreshes the tenant identity printed on receipts at startup and every six
/// hours. Failure is non-fatal: the cached identity and config.json fallback
/// keep the till printing.
/// </summary>
public sealed class CompanyProfileHostedService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly InterceptorConfig _config;
    private readonly CompanyProfileService _company;
    private readonly FiscalStackClient _client;
    private readonly ILog _log;

    public CompanyProfileHostedService(
        InterceptorConfig config,
        CompanyProfileService company,
        FiscalStackClient client,
        ILog log)
    {
        _config = config;
        _company = company;
        _client = client;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            return;
        }

        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _company.RefreshAsync(_client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            _log.Warn($"Company profile refresh failed: {ex.Message}");
        }
    }
}
