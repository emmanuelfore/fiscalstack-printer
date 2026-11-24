using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.Reporting;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// Tray mode runs the listener and the fiscalisation pipeline in a single
/// desktop process. The icon reports the live FDMS device state — green only for a
/// confirmed open fiscal day with no local work waiting — and the menu surfaces
/// urgent review, failure and sync alerts. A Windows service cannot host a tray
/// icon (session 0).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PrintInterceptorTray";
    private static readonly TimeSpan DeviceStatusInterval = TimeSpan.FromSeconds(30);

    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly SecretStore _secrets;
    private readonly FiscalStackClient _client;
    private readonly CompanyProfileService _company;
    private readonly OfflineSyncEngine _sync;
    private readonly OfflineReceiptService _offline;
    private readonly JobLedger _ledger;
    private readonly JobStore _jobs;
    private readonly PrintSnapshotStore _snapshots;
    private readonly FiscalDayReportService _dayReports;
    private readonly PrintJobPipeline _pipeline;
    private readonly PrintJobServer _server;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _dayItem;
    private readonly ToolStripMenuItem _openDayItem;
    private readonly ToolStripMenuItem _closeDayItem;
    private readonly ToolStripMenuItem _queueItem;
    private readonly ToolStripMenuItem _reviewItem;
    private readonly ToolStripMenuItem _failuresItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _capturePauseItem;
    private readonly ToolStripMenuItem _dashboardItem;
    private readonly ToolStripMenuItem _autostartItem;
    private DashboardForm? _dashboard;
    private readonly CancellationTokenSource _cts = new();
    private readonly Icon _onlineIcon;
    private readonly Icon _pendingIcon;
    private readonly Icon _offlineIcon;
    private FiscalDeviceStatus? _deviceStatus;
    private string? _deviceError;
    private DateTimeOffset _deviceStatusAt = DateTimeOffset.MinValue;
    private string? _lastCloseFailure;
    private bool _suppressNextCloseNotice;
    private bool _wasPaused;
    private bool _statusRefreshing;
    private bool _dayActionInFlight;
    private int _needsReview = -1;
    private int _rejectedCount = -1;
    private int _ambiguousCount = -1;
    private int _printFailedCount = -1;
    private bool _refreshing;

    public TrayApplicationContext(
        IServiceProvider services,
        InterceptorConfig config,
        ILog log,
        bool showInitialSetup = false)
    {
        _config = config;
        _log = log;
        _secrets = services.GetRequiredService<SecretStore>();
        _client = services.GetRequiredService<FiscalStackClient>();
        _company = services.GetRequiredService<CompanyProfileService>();
        _sync = services.GetRequiredService<OfflineSyncEngine>();
        _offline = services.GetRequiredService<OfflineReceiptService>();
        _ledger = services.GetRequiredService<JobLedger>();
        _jobs = services.GetRequiredService<JobStore>();
        _snapshots = services.GetRequiredService<PrintSnapshotStore>();
        _dayReports = services.GetRequiredService<FiscalDayReportService>();
        _pipeline = services.GetRequiredService<PrintJobPipeline>();
        _server = services.GetRequiredService<PrintJobServer>();

        _onlineIcon = CreateStatusIcon(Color.FromArgb(34, 197, 94));
        _pendingIcon = CreateStatusIcon(Color.FromArgb(245, 158, 11));
        _offlineIcon = CreateStatusIcon(Color.FromArgb(239, 68, 68));

        _statusItem = new ToolStripMenuItem("Checking fiscalisation…") { Enabled = false };
        _dayItem = new ToolStripMenuItem("Fiscal day: checking…") { Enabled = false };
        _openDayItem = new ToolStripMenuItem("Open fiscal day…", null, OpenDayFromTray) { Enabled = false };
        _closeDayItem = new ToolStripMenuItem("Close fiscal day…", null, CloseDayFromTray) { Enabled = false };
        _queueItem = new ToolStripMenuItem("Offline queue: empty") { Enabled = false };
        _reviewItem = new ToolStripMenuItem("Receipts needing review: 0", null, (_, _) => ShowJobs())
        {
            Visible = false
        };
        _failuresItem = new ToolStripMenuItem("Fiscalisation failures: 0", null, (_, _) => ShowJobs())
        {
            Visible = false
        };
        _pauseItem = new ToolStripMenuItem("Offline sync is PAUSED — resume…", null, ResumeSync)
        {
            Visible = false
        };
        _capturePauseItem = new ToolStripMenuItem("Pause capture", null, ToggleCapturePause);
        _dashboardItem = new ToolStripMenuItem("Show Dashboard", null, (_, _) => ShowDashboard());

        // The operator console owns the detail now. The tray stays deliberately short:
        // status (plus urgent alerts), the dashboard, capture pause and autostart.
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_reviewItem);
        menu.Items.Add(_failuresItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dashboardItem);
        menu.Items.Add(_capturePauseItem);
        menu.Items.Add(new ToolStripSeparator());

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, ToggleAutostart)
        {
            CheckOnClick = false
        };
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripMenuItem("Exit", null, Exit));

        _notifyIcon = new NotifyIcon
        {
            Icon = _offlineIcon,
            Text = "PrintInterceptor",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();
        menu.Opening += (_, _) => _autostartItem.Checked = IsAutostartEnabled();

        _timer = new System.Windows.Forms.Timer { Interval = 300 };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = 10_000;
            await RefreshAsync();
        };
        _timer.Start();

        if (showInitialSetup)
        {
            // Wait for the WinForms message loop before showing the modal dialog.
            var setupTimer = new System.Windows.Forms.Timer { Interval = 400 };
            setupTimer.Tick += (_, _) =>
            {
                setupTimer.Stop();
                setupTimer.Dispose();
                ShowDashboard("Settings");
            };
            setupTimer.Start();
        }
    }

    private async Task RefreshAsync(bool forceDevice = false)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var online = await _client.IsReachableAsync(_cts.Token);
            if (online && !string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                await RefreshDeviceStatusAsync(forceDevice);
            }
            else
            {
                _deviceError = online ? "API key required" : null;
                ApplyDeviceStatus(null);
            }

            var pending = _sync.PendingCount;
            var offlineReady = _offline.Ready;
            var paused = _sync.IsPaused;
            UpdateReviewCount();
            UpdateFailureAlerts();

            _statusItem.Text = FdmsStatusText(online);
            _pauseItem.Visible = paused;

            if (paused && !_wasPaused)
            {
                Balloon(
                    "Offline sync paused",
                    _sync.PauseReason ?? "A signed receipt was rejected. Reconcile it before resuming.",
                    ToolTipIcon.Error);
            }

            _wasPaused = paused;

            _queueItem.Text = paused
                ? pending == 0
                    ? "Offline queue: PAUSED — no receipts waiting"
                    : $"Offline queue: PAUSED — {pending} signed receipt(s) waiting"
                : pending == 0
                    ? "Offline queue: empty"
                    : $"Offline queue: {pending} signed receipt(s) waiting";
            _queueItem.Visible = _offline.Enabled || pending > 0 || paused;

            var capturePaused = _server.IsProcessingPaused;
            _capturePauseItem.Text = capturePaused ? "Resume capture" : "Pause capture";

            // The icon reports the fiscal device, not merely an HTTP ping. Green means FDMS
            // answered with an open fiscal day and this till has no local work waiting.
            var apiKeyMissing = string.IsNullOrWhiteSpace(_config.ApiKey);
            var fdmsKnown = online && !apiKeyMissing && _deviceStatus is not null;
            var fdmsReady = fdmsKnown && _deviceStatus!.IsDayOpen && !_deviceStatus.IsClosePending;
            var localAttention = paused || capturePaused || _failuresItem.Visible ||
                                 _reviewItem.Visible || pending > 0;
            _notifyIcon.Icon = !fdmsKnown
                ? _offlineIcon
                : fdmsReady && !localAttention
                    ? _onlineIcon
                    : _pendingIcon;

            var parts = new List<string> { FdmsStatusText(online) };
            if (_deviceStatus is { } dayStatus && fdmsKnown)
            {
                parts.Add(dayStatus.FiscalDayNo is int number ? $"day {number}" : "fiscal day unknown");
            }

            if (pending > 0) parts.Add($"{pending} queued");
            if (_needsReview > 0) parts.Add($"{_needsReview} to review");
            if (paused) parts.Add("sync PAUSED");
            if (capturePaused) parts.Add("capture PAUSED");
            if (!online)
            {
                if (!_offline.Enabled) parts.Add("offline disabled");
                else if (!offlineReady) parts.Add("offline not ready");
            }

            _notifyIcon.Text = Truncate("PrintInterceptor: " + string.Join(", ", parts), 63);
        }
        catch (OperationCanceledException)
        {
            // Exiting.
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray status refresh failed: {ex.Message}");
            _statusItem.Text = "Fiscalisation API: status unavailable";
            _notifyIcon.Icon = _offlineIcon;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// The tray's headline status is the fiscal device at FDMS/ZIMRA, not the API ping. Any
    /// API problem, missing key, failed status read or non-open day is spelled out here.
    /// </summary>
    private string FdmsStatusText(bool online)
    {
        if (!online)
        {
            return "FDMS: OFFLINE — FiscalStack API unreachable";
        }

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            return "FDMS: unavailable — API key required";
        }

        if (_deviceStatus is not { } status)
        {
            return string.IsNullOrWhiteSpace(_deviceError)
                ? "FDMS: unavailable — device status not checked"
                : "FDMS: unavailable — " + Truncate(_deviceError, 90);
        }

        var day = status.FiscalDayNo is int number ? $"day {number}" : "fiscal day";
        if (status.IsClosePending)
        {
            return $"FDMS: {day} close failed — retry close";
        }

        if (string.Equals(status.FiscalDayStatus, "FiscalDayCloseInitiated", StringComparison.OrdinalIgnoreCase))
        {
            return $"FDMS: {day} closing — wait for the gateway";
        }

        if (!status.IsDayOpen)
        {
            return string.IsNullOrWhiteSpace(status.FiscalDayStatus)
                ? "FDMS: device answered, fiscal day state unknown — check Fiscal status"
                : $"FDMS: {day} {status.FiscalDayStatus} — open a fiscal day before capturing";
        }

        return $"FDMS: ONLINE — {day} open";
    }

    private async Task RefreshDeviceStatusAsync(bool force)
    {
        if (_dayActionInFlight && !force)
        {
            return;
        }

        if (_statusRefreshing)
        {
            // A fetch that started before the action may be stale; make the next
            // periodic pass retry instead of trusting its result.
            if (force)
            {
                _deviceStatusAt = DateTimeOffset.MinValue;
            }

            return;
        }

        if (!force && DateTimeOffset.UtcNow - _deviceStatusAt < DeviceStatusInterval)
        {
            return;
        }

        _statusRefreshing = true;
        _deviceStatusAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await _client.GetDeviceStatusAsync(_cts.Token);
            _deviceError = result.Success ? null : result.Error;
            ApplyDeviceStatus(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray device status refresh failed: {ex.Message}");
            _deviceError = ex.Message;
            ApplyDeviceStatus(null);
        }
        finally
        {
            _statusRefreshing = false;
        }
    }

    private void ApplyDeviceStatus(DeviceStatusResult? result)
    {
        if (_dayActionInFlight)
        {
            return;
        }

        if (result is not { Success: true, Status: not null })
        {
            _deviceStatus = null;
            _dayItem.Text = string.IsNullOrWhiteSpace(_config.ApiKey)
                ? "Fiscal day: API key required"
                : "Fiscal day: unavailable";
            _openDayItem.Enabled = false;
            _closeDayItem.Enabled = false;
            return;
        }

        var status = result.Status;
        _deviceStatus = status;

        var state = status.FiscalDayStatus switch
        {
            "FiscalDayOpened" => "open",
            "FiscalDayCloseFailed" => "open — last close failed",
            "FiscalDayCloseInitiated" => "closing…",
            "FiscalDayClosed" => "closed",
            _ => string.IsNullOrWhiteSpace(status.FiscalDayStatus) ? "unknown" : status.FiscalDayStatus
        };

        _dayItem.Text = status.FiscalDayNo is int day
            ? $"Fiscal day {day}: {state}"
            : $"Fiscal day: {state}";
        _openDayItem.Enabled = !status.IsDayOpen;
        _closeDayItem.Enabled = status.IsDayOpen;
        _closeDayItem.Text = status.IsClosePending ? "Retry close fiscal day…" : "Close fiscal day…";

        NotifyCloseFailure(status);
    }

    private void NotifyCloseFailure(FiscalDeviceStatus status)
    {
        var closingFailed = string.Equals(
            status.FiscalDayStatus, "FiscalDayCloseFailed", StringComparison.OrdinalIgnoreCase);
        var failure = status.IsDayOpen && (closingFailed || status.FiscalDayClosingErrorCode is not null)
            ? $"{status.FiscalDayStatus}|{status.FiscalDayClosingErrorCode}"
            : null;

        if (failure is not null && failure != _lastCloseFailure)
        {
            if (_suppressNextCloseNotice)
            {
                _suppressNextCloseNotice = false;
            }
            else
            {
                var code = status.FiscalDayClosingErrorCode;
                var detail = code is null
                    ? ""
                    : $" {code} — {FiscalStatusForm.DescribeCloseError(code)}";
                Balloon(
                    "Fiscal day close failed",
                    $"Day {(status.FiscalDayNo?.ToString() ?? "?")} is still open.{detail} Close it again from the tray.",
                    ToolTipIcon.Warning);
            }
        }

        _lastCloseFailure = failure;
    }

    private void UpdateReviewCount()
    {
        int count;
        try
        {
            count = _ledger.Count(LedgerStatus.NeedsReview);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not count receipts needing review: {ex.Message}");
            return;
        }

        if (_needsReview >= 0 && count > _needsReview)
        {
            LedgerEntry? latest = null;
            try
            {
                latest = _ledger.All()
                    .Where(entry => entry.Status == LedgerStatus.NeedsReview)
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not read the latest held receipt: {ex.Message}");
            }

            var reason = latest is null
                ? "Open Fiscalisation jobs for the recorded reason."
                : $"{(string.IsNullOrWhiteSpace(latest.ReviewStage) ? "Review" : latest.ReviewStage)}: " +
                  (string.IsNullOrWhiteSpace(latest.Error) ? "cause not recorded" : latest.Error) +
                  (string.IsNullOrWhiteSpace(latest.ReviewHint) ? "" : " Next: " + latest.ReviewHint);
            Balloon(
                "Receipt held for review",
                $"{count - _needsReview} new receipt(s) were not fiscalised. {reason}",
                ToolTipIcon.Warning);
        }

        _needsReview = count;
        _reviewItem.Text = $"Receipts needing review: {count}";
        _reviewItem.Visible = count > 0;
    }

    /// <summary>
    /// Raises a balloon when a new fiscalisation rejection, an ambiguous
    /// outcome, or a print failure appears in the ledger. The counts are
    /// remembered between refreshes so the operator only sees one alert per
    /// new failure, and the tray tooltip keeps a running total when several
    /// are outstanding.
    /// </summary>
    private void UpdateFailureAlerts()
    {
        FailureSnapshot snapshot;
        try
        {
            snapshot = _ledger.GetFailureSnapshot();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the failure snapshot: {ex.Message}");
            return;
        }

        var firstScan = _rejectedCount < 0;

        if (!firstScan)
        {
            if (snapshot.Rejected > _rejectedCount)
            {
                Balloon(
                    "Fiscalisation rejected",
                    DescribeFailure(snapshot.LatestRejected, $"{snapshot.Rejected - _rejectedCount} new receipt(s)"),
                    ToolTipIcon.Error);
            }

            if (snapshot.Ambiguous > _ambiguousCount)
            {
                Balloon(
                    "Fiscalisation outcome unknown",
                    DescribeFailure(snapshot.LatestAmbiguous, $"{snapshot.Ambiguous - _ambiguousCount} new receipt(s)") +
                    " Reconcile with FiscalStack before resubmitting.",
                    ToolTipIcon.Error);
            }

            if (snapshot.PrintFailed > _printFailedCount)
            {
                Balloon(
                    "Fiscal receipt did not print",
                    DescribeFailure(snapshot.LatestPrintFailure, $"{snapshot.PrintFailed - _printFailedCount} new receipt(s)") +
                    " Use Fiscalisation jobs to reprint once the printer is back.",
                    ToolTipIcon.Warning);
            }
        }

        _rejectedCount = snapshot.Rejected;
        _ambiguousCount = snapshot.Ambiguous;
        _printFailedCount = snapshot.PrintFailed;

        var total = snapshot.Rejected + snapshot.Ambiguous + snapshot.PrintFailed;
        var breakdown = new List<string>();
        if (snapshot.Rejected > 0) breakdown.Add($"{snapshot.Rejected} rejected");
        if (snapshot.Ambiguous > 0) breakdown.Add($"{snapshot.Ambiguous} unknown");
        if (snapshot.PrintFailed > 0) breakdown.Add($"{snapshot.PrintFailed} unprinted");
        _failuresItem.Text = total == 0
            ? "Fiscalisation failures: none"
            : $"Fiscalisation failures: {total} ({string.Join(", ", breakdown)})";
        _failuresItem.Visible = total > 0;
    }

    private static string DescribeFailure(LedgerEntry? entry, string fallback) =>
        entry is null
            ? $"{fallback} need attention."
            : $"{(string.IsNullOrWhiteSpace(entry.InvoiceNumber) ? $"Job {Short(entry.Hash)}" : $"Invoice {entry.InvoiceNumber}")}" +
              $": {Truncate(entry.Error ?? entry.PrintError ?? "No further detail was recorded.", 180)}";

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private async void SyncNow(object? sender, EventArgs e)
    {
        if (_sync.IsPaused)
        {
            _wasPaused = true;
            Balloon(
                "Offline sync is paused",
                (_sync.PauseReason ?? "A receipt was rejected.") +
                " Reconcile it with FiscalStack, then choose Resume offline sync.",
                ToolTipIcon.Warning);
            return;
        }

        if (!_offline.Enabled && !_sync.HasPending)
        {
            Balloon("Offline signing is disabled", "Enable it in Settings to queue signed receipts while offline.",
                ToolTipIcon.Info);
            return;
        }

        try
        {
            var result = await _sync.SyncNowAsync(_cts.Token);

            // The balloon below already reports a pause caused by this run, so let
            // the periodic transition check start from the new state.
            _wasPaused = _sync.IsPaused;

            if (result.Remaining > 0)
            {
                Balloon(
                    "Offline sync paused",
                    result.Error ?? $"{result.Remaining} receipt(s) still pending.",
                    ToolTipIcon.Warning);
            }
            else
            {
                Balloon(
                    result.Synced > 0 ? "Offline receipts synced" : "Nothing to sync",
                    result.Synced > 0 ? $"{result.Synced} receipt(s) accepted by FiscalStack." : "The queue is empty.",
                    ToolTipIcon.Info);
            }

            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Manual offline sync failed", ex);
            Balloon("Offline sync failed", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Settles receipts the till could not confirm — a timeout or a server error leaves the sale
    /// neither printed nor refused. This pass ignores reconcile.enabled and the attempt budget:
    /// an operator pressing it is the reason the manual path exists.
    /// </summary>
    private async void ReconcileNow(object? sender, EventArgs e)
    {
        try
        {
            var report = await _pipeline.ReconcileAsync(_cts.Token, force: true);

            if (report.Scanned == 0)
            {
                Balloon(
                    "Nothing to reconcile",
                    report.Error ?? "No receipt is waiting for an answer from FiscalStack.",
                    ToolTipIcon.Info);
            }
            else
            {
                Balloon(
                    "Reconciliation finished",
                    report.Summary,
                    report.StillUnknown > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }

            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Manual reconciliation failed", ex);
            Balloon("Reconciliation failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ResumeSync(object? sender, EventArgs e)
    {
        var reason = _sync.PauseReason ?? "The queue is paused.";
        var answer = MessageBox.Show(
            "Resume the offline sync queue?" + Environment.NewLine + Environment.NewLine +
            "Only do this after the rejected receipt has been reconciled in FiscalStack/ZIMRA. " +
            "Queued receipts resume with the counters they were signed with." + Environment.NewLine +
            Environment.NewLine + "Pause reason: " + reason,
            "Resume offline sync",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _sync.Resume();
        _wasPaused = false;
        _log.Warn($"Offline sync resumed by the operator. Previous pause: {reason}");
        Balloon("Offline sync resumed", "Queued receipts will be retried on the next sync.", ToolTipIcon.Info);
        _ = RefreshAsync();
    }

    private async void OpenDayFromTray(object? sender, EventArgs e)
    {
        if (_deviceStatus is not { IsDayOpen: false })
        {
            Balloon("Cannot open a fiscal day", "A day is already open, or its state is unknown.", ToolTipIcon.Warning);
            return;
        }

        _dayActionInFlight = true;
        var answer = MessageBox.Show(
            "Open a new fiscal day?" + Environment.NewLine + Environment.NewLine +
            "The previous day must already be closed. New receipts will use the next fiscal day number.",
            "Open fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            _dayActionInFlight = false;
            return;
        }

        _dayItem.Text = "Fiscal day: opening…";
        _openDayItem.Enabled = false;
        _closeDayItem.Enabled = false;
        try
        {
            var result = await _client.OpenFiscalDayAsync(_cts.Token);
            if (result.Success)
            {
                _log.Info($"Fiscal day opened from the tray. {result.Message}");
                MessageBox.Show(result.Message, "Fiscal day opened",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _log.Warn($"Opening the fiscal day failed: {result.Message}");
                MessageBox.Show(result.Message, "Could not open the fiscal day",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _dayActionInFlight = false;
        }

        await RefreshAsync(forceDevice: true);
    }

    private async void CloseDayFromTray(object? sender, EventArgs e)
    {
        if (_deviceStatus is not { IsDayOpen: true })
        {
            Balloon("Cannot close the fiscal day", "No fiscal day is open, or its state is unknown.",
                ToolTipIcon.Warning);
            return;
        }

        _dayActionInFlight = true;
        var answer = MessageBox.Show(
            "Close the fiscal day?" + Environment.NewLine + Environment.NewLine +
            "Every receipt for the day must be fiscalised; missing or rejected receipts (grey/red) " +
            "make the gateway reject the close. Closure can take up to a minute." + Environment.NewLine +
            Environment.NewLine + "Close the fiscal day now?",
            "Close fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            _dayActionInFlight = false;
            return;
        }

        _dayItem.Text = "Fiscal day: closing…";
        _openDayItem.Enabled = false;
        _closeDayItem.Enabled = false;
        try
        {
            var result = await _client.CloseFiscalDayAsync(_cts.Token);
            if (result.Success)
            {
                _log.Info($"Fiscal day closed from the tray. {result.Message}");
                MessageBox.Show(result.Message, "Fiscal day closed",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                // The dialog already told the operator; do not repeat it as a balloon.
                _suppressNextCloseNotice = true;
                var detail = result.ErrorCode is null
                    ? result.Message
                    : $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
                      $"Gateway error: {result.ErrorCode} — {FiscalStatusForm.DescribeCloseError(result.ErrorCode)}";

                _log.Warn($"Closing the fiscal day failed: {result.Message} ({result.ErrorCode ?? "no code"})");
                MessageBox.Show(detail,
                    result.Pending ? "Closure not confirmed yet" : "Could not close the fiscal day",
                    MessageBoxButtons.OK,
                    result.Pending ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _dayActionInFlight = false;
        }

        await RefreshAsync(forceDevice: true);
        _suppressNextCloseNotice = false;
    }

    private async void TestConnection(object? sender, EventArgs e)
    {
        try
        {
            var online = await _client.IsReachableAsync(CancellationToken.None);
            MessageBox.Show(
                online
                    ? $"FiscalStack is reachable at{Environment.NewLine}{_config.ApiEndpoint}"
                    : $"No response from{Environment.NewLine}{_config.ApiEndpoint}{Environment.NewLine}{Environment.NewLine}" +
                      "Print receipts will still be signed offline when that is enabled.",
                "PrintInterceptor",
                MessageBoxButtons.OK,
                online ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Connection test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowFiscalStatus()
    {
        using var form = new FiscalStatusForm(_config, _client, _company, _log);
        form.ShowDialog();
        _deviceStatusAt = DateTimeOffset.MinValue;
        _ = RefreshAsync(forceDevice: true);
    }

    private void ShowJobs()
    {
        using var form = new JobsForm(_ledger, _snapshots, _pipeline, _jobs, _log);
        form.ShowDialog();
    }

    /// <summary>
    /// The day's fiscal position and the accountant's line export, read from what this till keeps.
    /// </summary>
    private void ShowDayReport()
    {
        using var form = new DayReportForm(_dayReports, _config.ResolveDataDirectory());
        form.ShowDialog();
    }

    /// <summary>
    /// Onboarding for a POS no profile describes: one of the day's caught jobs becomes the layout
    /// rules, shown as evidence before anything is written. Applying it changes how every later job
    /// is read, so the cached status probe is dropped the same way Settings does.
    /// </summary>
    private void ShowCapture()
    {
        using var form = new ProfileCaptureForm(_ledger, _jobs, _config, Profiles, _log);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        // Applying a captured profile edits the same config the running parser reads.
        _pipeline.ReloadParser();
        _deviceStatusAt = DateTimeOffset.MinValue;
        _ = RefreshAsync(forceDevice: false);
    }

    /// <summary>
    /// The operator console is the only place with detail now. It stays open beside the till;
    /// the tray item and icon double-click toggle it, and first-run opens straight to Settings.
    /// </summary>
    private void ShowDashboard(string? initialPage = null)
    {
        if (_dashboard is { IsDisposed: false } dashboard)
        {
            if (initialPage is not null)
            {
                dashboard.NavigateTo(initialPage);
                dashboard.Show();
                dashboard.Activate();
            }
            else if (dashboard.Visible)
            {
                dashboard.Hide();
            }
            else
            {
                dashboard.Show();
                dashboard.Activate();
            }
        }
        else
        {
            var actions = new DashboardActions(
                () => new SettingsForm(_config, _log, _offline, _secrets),
                RunDashboardTool,
                () => new JobsForm(_ledger, _snapshots, _pipeline, _jobs, _log),
                () => new DayReportForm(_dayReports, _config.ResolveDataDirectory()));
            _dashboard = new DashboardForm(
                _config, _client, _company, _ledger, _sync, _snapshots, _pipeline, _jobs, _log,
                actions, _server, initialPage ?? "Overview");
            _dashboard.FormClosed += (_, _) =>
            {
                _dashboard = null;
                _dashboardItem.Text = "Show Dashboard";
                _deviceStatusAt = DateTimeOffset.MinValue;
                _ = RefreshAsync(forceDevice: true);
            };
            _dashboard.Show();
        }

        _dashboardItem.Text = _dashboard is { Visible: true } ? "Hide Dashboard" : "Show Dashboard";
    }

    /// <summary>
    /// One entry point for every action the console sidebar can trigger. The tray menu no longer
    /// duplicates these; only genuinely urgent alerts remain visible beside the dashboard toggle.
    /// </summary>
    private bool TryOpenDashboardPage(string page)
    {
        if (_dashboard is not { IsDisposed: false } dashboard)
        {
            return false;
        }

        dashboard.NavigateTo(page);
        dashboard.Show();
        dashboard.Activate();
        return true;
    }

    private void RunDashboardTool(string tool)
    {
        switch (tool)
        {
            case "toggle-capture":
                ToggleCapturePause(null, EventArgs.Empty);
                break;
            case "jobs":
                if (!TryOpenDashboardPage("Jobs"))
                {
                    ShowJobs();
                }
                break;
            case "fiscal-status":
                ShowFiscalStatus();
                break;
            case "day-report":
                if (!TryOpenDashboardPage("Reports"))
                {
                    ShowDayReport();
                }
                break;
            case "capture":
                ShowCapture();
                break;
            case "updates":
                CheckForUpdates(null, EventArgs.Empty);
                break;
            case "sync":
                SyncNow(null, EventArgs.Empty);
                break;
            case "reconcile":
                ReconcileNow(null, EventArgs.Empty);
                break;
            case "test-connection":
                TestConnection(null, EventArgs.Empty);
                break;
            case "import-profile":
                ImportParseProfile(null, EventArgs.Empty);
                break;
            case "profiles":
                OpenParseProfiles(null, EventArgs.Empty);
                break;
            case "open-config":
                OpenConfig(null, EventArgs.Empty);
                break;
            case "data":
                OpenData(null, EventArgs.Empty);
                break;
            case "received":
                OpenReceivedFiles(null, EventArgs.Empty);
                break;
            case "fiscalised":
                OpenFiscalisedInvoices(null, EventArgs.Empty);
                break;
            case "logs":
                OpenLogs(null, EventArgs.Empty);
                break;
            case "current-log":
                OpenCurrentLogFile(null, EventArgs.Empty);
                break;
            case "autostart":
                ToggleAutostart(null, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Holds fiscalisation without refusing prints: the spooler still delivers, jobs queue,
    /// and everything captured while paused is processed on resume. Shutdown always resumes.
    /// </summary>
    private void ToggleCapturePause(object? sender, EventArgs e)
    {
        if (_server.IsProcessingPaused)
        {
            _server.ResumeProcessing();
            Balloon("Capture resumed", "Jobs captured while paused are being fiscalised now.", ToolTipIcon.Info);
        }
        else
        {
            _server.PauseProcessing();
            Balloon(
                "Capture paused",
                "Prints are still captured and queue, but nothing is fiscalised until you resume. " +
                "Do not leave a till paused at closing time.",
                ToolTipIcon.Warning);
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Runs the bundled updater in dry-run mode: it downloads the manifest's installer, checks
    /// SHA256 and the pinned Authenticode signer and reports the till's health, but installs
    /// nothing. Installing stays with the elevated scheduled task.
    /// </summary>
    private async void CheckForUpdates(object? sender, EventArgs e)
    {
        var pin = _config.Update.SignerThumbprint;
        if (pin.Length == 0)
        {
            MessageBox.Show(
                "No release signer is pinned yet." + Environment.NewLine + Environment.NewLine +
                "Set update.signerThumbprint in the configuration file to the thumbprint of the " +
                "certificate that signs FiscalStack releases, then check again.",
                "Check for updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "updater", "auto-update.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(
                $"The updater script was not found at:{Environment.NewLine}{script}",
                "Check for updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var output = Path.Combine(Path.GetTempPath(), $"printinterceptor-update-check-{Guid.NewGuid():N}.log");
        var command =
            "$ErrorActionPreference='Stop'; " +
            $"try {{ & '{Ps(script)}' -DryRun -AllowTrayUpdate -SignerThumbprint '{Ps(pin)}' " +
            $"-ManifestUrl '{Ps(_config.Update.ManifestUrl)}' -Channel '{Ps(_config.Update.Channel)}' " +
            $"*>&1 | Out-File -FilePath '{Ps(output)}' -Encoding utf8; " +
            $"Add-Content -Path '{Ps(output)}' -Value 'CHECK_EXIT=0' }} " +
            $"catch {{ Add-Content -Path '{Ps(output)}' -Value ($_ | Out-String); " +
            $"Add-Content -Path '{Ps(output)}' -Value 'CHECK_EXIT=1' }}";

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(_cts.Token);

            var text = File.Exists(output) ? File.ReadAllText(output) : "";
            var succeeded = text.Contains("CHECK_EXIT=0", StringComparison.Ordinal);
            var message = text.Replace("CHECK_EXIT=0", "").Replace("CHECK_EXIT=1", "").Trim();
            if (message.Length == 0)
            {
                message = "The updater produced no output.";
            }

            MessageBox.Show(
                Truncate(message, 1800),
                succeeded ? "Update check passed" : "Update check failed",
                MessageBoxButtons.OK,
                succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The operator dismissed the elevation prompt.
            Balloon("Update check cancelled", "The check needs administrator rights; nothing was changed.", ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Checking for updates failed", ex);
            MessageBox.Show(ex.Message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    /// <summary>A path inside a single-quoted PowerShell string.</summary>
    private static string Ps(string value) => value.Replace("'", "''");

    private void OpenCurrentLogFile(object? sender, EventArgs e)
    {
        var logs = Path.Combine(_config.ResolveDataDirectory(), "logs");
        Directory.CreateDirectory(logs);
        var newest = Directory.GetFiles(logs, "printinterceptor-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            MessageBox.Show("No log file has been written yet.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ShellOpen.Path(newest.FullName);
    }

    private void OpenConfig(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.ConfigPath) || !File.Exists(_config.ConfigPath))
        {
            MessageBox.Show("The configuration file does not exist yet.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ShellOpen.Path(_config.ConfigPath);
    }

    private void OpenReceivedFiles(object? sender, EventArgs e)
    {
        var path = Path.Combine(_config.ResolveDataDirectory(), "jobs");
        Directory.CreateDirectory(path);
        ShellOpen.Path(path);
    }

    private void OpenFiscalisedInvoices(object? sender, EventArgs e)
    {
        var path = Path.Combine(_config.ResolveDataDirectory(), "fiscalised");
        Directory.CreateDirectory(path);
        ShellOpen.Path(path);
    }

    private void OpenLogs(object? sender, EventArgs e)
    {
        var path = Path.Combine(_config.ResolveDataDirectory(), "logs");
        Directory.CreateDirectory(path);
        ShellOpen.Path(path);
    }

    private void OpenData(object? sender, EventArgs e)
    {
        var path = _config.ResolveDataDirectory();
        Directory.CreateDirectory(path);
        ShellOpen.Path(path);
    }

    /// <summary>
    /// Installs a profile file received from another till. Importing deliberately does
    /// not apply it: the operator picks the layout on the console's Parse rules page, so a
    /// wrong file cannot silently change what the next receipt is read as.
    /// </summary>
    private void ImportParseProfile(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a receipt parse profile",
            Filter = "Parse profile (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            var installed = Profiles.Import(dialog.FileName);
            _log.Info($"Imported parse profile '{Path.GetFileNameWithoutExtension(installed)}' to {installed}.");
            if (MessageBox.Show(
                    $"Installed {Path.GetFileName(installed)}.\n\n" +
                    "Open Parse rules and pick it under Installed profiles to use its rules.",
                    "Parse profile imported",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) == DialogResult.OK)
            {
                ShowDashboard("Parse rules");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"A parse profile could not be imported: {ex.Message}");
            MessageBox.Show(ex.Message, "Could not import the profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenParseProfiles(object? sender, EventArgs e) => ShellOpen.Folder(Profiles.UserDirectory);

    private ParseProfileStore? _profiles;

    private ParseProfileStore Profiles => _profiles ??= ParseProfileStore.ForCurrentInstall();

    private void ToggleAutostart(object? sender, EventArgs e)
    {
        try
        {
            SetAutostart(!IsAutostartEnabled());
            _autostartItem.Checked = IsAutostartEnabled();
        }
        catch (Exception ex)
        {
            _log.Error("Could not change the start-with-Windows setting", ex);
            MessageBox.Show(ex.Message, "PrintInterceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string value && value.Length > 0;
    }

    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open the Windows startup registry key.");
        }

        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\" --tray");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        _notifyIcon.ShowBalloonTip(5000, title, Truncate(text, 250), icon);

    private void Exit(object? sender, EventArgs e)
    {
        _cts.Cancel();
        ExitThread();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static Icon CreateStatusIcon(Color statusColor)
    {
        using var baseIcon = LoadTrayBaseIcon();
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            if (baseIcon is not null)
            {
                // Leave the lower-right corner free for the connection badge.
                graphics.DrawImage(baseIcon, new Rectangle(1, 1, 28, 28));
            }
            else
            {
                using var fallback = new SolidBrush(Color.FromArgb(29, 78, 216));
                graphics.FillEllipse(fallback, 3, 3, 26, 26);
                using var fallbackText = new Pen(Color.White, 2.5f);
                graphics.DrawEllipse(fallbackText, 3, 3, 26, 26);
            }

            const int badgeSize = 14;
            const int badgeLeft = 32 - badgeSize;
            const int badgeTop = 32 - badgeSize;
            using var glow = new SolidBrush(Color.FromArgb(90, statusColor));
            graphics.FillEllipse(glow, badgeLeft - 2, badgeTop - 2, badgeSize + 4, badgeSize + 4);
            using var badge = new SolidBrush(statusColor);
            graphics.FillEllipse(badge, badgeLeft, badgeTop, badgeSize, badgeSize);
            using var badgeOutline = new Pen(Color.White, 2f);
            graphics.DrawEllipse(badgeOutline, badgeLeft, badgeTop, badgeSize, badgeSize);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap? LoadTrayBaseIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(
                "app-icon.ico", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return null;
        }

        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return null;
        }

        using var icon = new Icon(resource);
        return (Bitmap)icon.ToBitmap().Clone();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _timer.Stop();
            _timer.Dispose();
            if (_dashboard is { IsDisposed: false } dashboard)
            {
                dashboard.Close();
                dashboard.Dispose();
            }
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _onlineIcon.Dispose();
            _pendingIcon.Dispose();
            _offlineIcon.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
