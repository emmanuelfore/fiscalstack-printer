using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// One-page operator view of the till: today's numbers, the hour's activity, the latest jobs and
/// the fiscal-day position with the close-day checks. It reads the same local ledger the service
/// writes, so it works when the network is down; only the device card needs FiscalStack.
/// </summary>
public sealed class DashboardForm : Form
{
    private static readonly Color OkColor = Color.FromArgb(22, 101, 52);
    private static readonly Color WarnColor = Color.FromArgb(180, 83, 9);
    private static readonly Color BadColor = Color.FromArgb(185, 28, 28);
    private static readonly Color MutedColor = Color.FromArgb(100, 116, 139);

    private readonly InterceptorConfig _config;
    private readonly FiscalStackClient _client;
    private readonly CompanyProfileService _company;
    private readonly JobLedger _ledger;
    private readonly OfflineSyncEngine _sync;
    private readonly PrintSnapshotStore _snapshots;
    private readonly PrintJobPipeline _pipeline;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _clock;

    private readonly Label _identity;
    private readonly Label _kpiReceipts;
    private readonly Label _kpiQueued;
    private readonly Label _kpiReview;
    private readonly Label _kpiTotal;
    private readonly HourlyChart _chart;
    private readonly ListView _recent;
    private readonly Label _recentCount;
    private readonly ComboBox _recentFilter;
    private readonly Label _dayTitle;
    private readonly Label _dayMeta;
    private readonly Label _dayTimer;
    private readonly Label _checkFdms;
    private readonly Label _checkQueue;
    private readonly Label _checkReview;
    private readonly Button _openDay;
    private readonly Button _closeDay;

    private FiscalDeviceStatus? _deviceStatus;
    private DateTimeOffset _deviceStatusAt = DateTimeOffset.MinValue;
    private bool _refreshing;
    private bool _dayActionInFlight;

    public DashboardForm(
        InterceptorConfig config,
        FiscalStackClient client,
        CompanyProfileService company,
        JobLedger ledger,
        OfflineSyncEngine sync,
        PrintSnapshotStore snapshots,
        PrintJobPipeline pipeline,
        ILog log)
    {
        _config = config;
        _client = client;
        _company = company;
        _ledger = ledger;
        _sync = sync;
        _snapshots = snapshots;
        _pipeline = pipeline;
        _log = log;

        Text = "PrintInterceptor — Dashboard";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(940, 600);
        ShowInTaskbar = true;

        _identity = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(12, 10, 3, 3)
        };

        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(92, 30) };
        refresh.Click += (_, _) => _ = RefreshAsync(forceDevice: true);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 2)
        };
        header.Controls.Add(_identity);
        header.Controls.Add(refresh);

        _kpiReceipts = KpiValue();
        _kpiQueued = KpiValue();
        _kpiReview = KpiValue();
        _kpiTotal = KpiValue();

        var kpis = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 92,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(10, 4, 10, 6)
        };
        for (var column = 0; column < 4; column++)
        {
            kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        }

        kpis.Controls.Add(KpiCard("Receipts today", _kpiReceipts), 0, 0);
        kpis.Controls.Add(KpiCard("Queued / retrying", _kpiQueued), 1, 0);
        kpis.Controls.Add(KpiCard("Needs review", _kpiReview), 2, 0);
        kpis.Controls.Add(KpiCard("Today's total", _kpiTotal), 3, 0);

        _chart = new HourlyChart
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 6, 12, 6)
        };

        var chartHost = new GroupBox
        {
            Text = "Fiscalised receipts by hour (today)",
            Dock = DockStyle.Top,
            Height = 150,
            Padding = new Padding(8),
            Margin = new Padding(10, 2, 10, 4)
        };
        chartHost.Controls.Add(_chart);

        _recentFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        _recentFilter.Items.AddRange(new object[] { "All jobs", "Needs review", "Failures", "Fiscalised" });
        _recentFilter.SelectedIndex = 0;
        _recentFilter.SelectedIndexChanged += (_, _) => FillRecent();

        _recentCount = new Label { AutoSize = true, ForeColor = MutedColor, Margin = new Padding(10, 8, 3, 3) };

        var recentToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 4, 6, 2)
        };
        recentToolbar.Controls.Add(new Label { Text = "Show:", AutoSize = true, Margin = new Padding(3, 8, 6, 3) });
        recentToolbar.Controls.Add(_recentFilter);
        recentToolbar.Controls.Add(_recentCount);

        _recent = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false
        };
        _recent.Columns.Add("Updated", 135);
        _recent.Columns.Add("Invoice", 150);
        _recent.Columns.Add("Type", 90);
        _recent.Columns.Add("Status", 130);
        _recent.Columns.Add("Total", 100, HorizontalAlignment.Right);
        _recent.Columns.Add("Print", 100);
        _recent.DoubleClick += (_, _) => OpenJobs();

        var recentHost = new GroupBox
        {
            Text = "Recent fiscalisation jobs (double-click to open the jobs window)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(10, 4, 6, 8)
        };
        recentHost.Controls.Add(_recent);
        recentHost.Controls.Add(recentToolbar);

        _dayTitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(8, 6, 3, 2)
        };
        _dayMeta = new Label { AutoSize = true, ForeColor = MutedColor, Margin = new Padding(8, 2, 3, 2) };
        _dayTimer = new Label
        {
            AutoSize = true,
            Font = new Font("Consolas", 12f),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(8, 2, 3, 6)
        };

        var dayCard = new GroupBox
        {
            Text = "Fiscal day",
            Dock = DockStyle.Top,
            Height = 118,
            Padding = new Padding(6),
            Margin = new Padding(6, 4, 10, 4)
        };
        var dayStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };
        dayStack.Controls.Add(_dayTitle);
        dayStack.Controls.Add(_dayMeta);
        dayStack.Controls.Add(_dayTimer);
        dayCard.Controls.Add(dayStack);

        _checkFdms = CheckLabel();
        _checkQueue = CheckLabel();
        _checkReview = CheckLabel();

        _openDay = new Button { Text = "Open fiscal day…", AutoSize = true, Enabled = false };
        _openDay.Click += (_, _) => _ = OpenDayAsync();
        _closeDay = new Button { Text = "Close fiscal day…", AutoSize = true, Enabled = false };
        _closeDay.Click += (_, _) => _ = CloseDayAsync();

        var dayButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(6, 4, 6, 4)
        };
        dayButtons.Controls.Add(_closeDay);
        dayButtons.Controls.Add(_openDay);

        var checklist = new GroupBox
        {
            Text = "Close-day checks",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(6, 4, 10, 8)
        };
        var checkStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(4)
        };
        checkStack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
        checkStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        checkStack.Controls.Add(new Label { Text = "FDMS", AutoSize = true, ForeColor = MutedColor }, 0, 0);
        checkStack.Controls.Add(_checkFdms, 1, 0);
        checkStack.Controls.Add(new Label { Text = "Queue", AutoSize = true, ForeColor = MutedColor }, 0, 1);
        checkStack.Controls.Add(_checkQueue, 1, 1);
        checkStack.Controls.Add(new Label { Text = "Review", AutoSize = true, ForeColor = MutedColor }, 0, 2);
        checkStack.Controls.Add(_checkReview, 1, 2);
        checklist.Controls.Add(checkStack);
        checklist.Controls.Add(dayButtons);

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 126f));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        side.Controls.Add(dayCard, 0, 0);
        side.Controls.Add(checklist, 0, 1);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330f));
        content.Controls.Add(recentHost, 0, 0);
        content.Controls.Add(side, 1, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 160f));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        main.Controls.Add(chartHost, 0, 0);
        main.Controls.Add(content, 0, 1);

        Controls.Add(main);
        Controls.Add(kpis);
        Controls.Add(header);

        _timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => UpdateDayTimer();
        _clock.Start();

        FormClosed += (_, _) =>
        {
            _cts.Cancel();
            _timer.Stop();
            _timer.Dispose();
            _clock.Stop();
            _clock.Dispose();
            _cts.Dispose();
        };

        _ = RefreshAsync(forceDevice: true);
    }

    private static Label KpiValue() => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 15f, FontStyle.Bold),
        ForeColor = Color.FromArgb(15, 23, 42),
        Margin = new Padding(10, 2, 3, 6)
    };

    private static Panel KpiCard(string title, Label value)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(4, 2, 4, 2),
            Padding = new Padding(2)
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240));
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };
        stack.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = MutedColor,
            Margin = new Padding(10, 8, 3, 0)
        });
        stack.Controls.Add(value);
        card.Controls.Add(stack);
        return card;
    }

    private static Label CheckLabel() => new()
    {
        AutoSize = true,
        ForeColor = MutedColor,
        Margin = new Padding(3, 4, 3, 4)
    };

    private async Task RefreshAsync(bool forceDevice = false)
    {
        if (_refreshing || _cts.IsCancellationRequested)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var entries = _ledger.All();
            var today = DateTimeOffset.Now.Date;
            var todayEntries = entries.Where(entry => entry.CreatedAt.Date == today).ToList();
            var fiscalisedToday = todayEntries
                .Where(entry => entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline)
                .ToList();

            _identity.Text = $"{_company.Name} · {_config.ListenAddress}:{_config.ListenPort}";
            _kpiReceipts.Text = fiscalisedToday.Count.ToString();
            _kpiQueued.Text = (_sync.PendingCount + entries.Count(entry => entry.Status == LedgerStatus.InFlight)).ToString();
            _kpiReview.Text = entries.Count(entry => entry.Status == LedgerStatus.NeedsReview).ToString();

            var totals = fiscalisedToday
                .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Currency) ? "—" : entry.Currency)
                .Select(group => $"{group.Sum(entry => entry.Total):0.00} {group.Key}");
            _kpiTotal.Text = string.Join(" / ", totals.DefaultIfEmpty("—"));
            _kpiTotal.Font = new Font(_kpiTotal.Font.FontFamily, totals.Any() && _kpiTotal.Text.Length > 22 ? 10.5f : 15f, FontStyle.Bold);

            var hours = new int[24];
            foreach (var entry in fiscalisedToday)
            {
                hours[entry.CreatedAt.Hour]++;
            }

            _chart.SetCounts(hours);
            FillRecent(entries);
            UpdateChecklist(entries);

            if (forceDevice || DateTimeOffset.UtcNow - _deviceStatusAt > TimeSpan.FromSeconds(30))
            {
                _deviceStatusAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                {
                    try
                    {
                        var result = await _client.GetDeviceStatusAsync(_cts.Token);
                        _deviceStatus = result is { Success: true, Status: not null } ? result.Status : null;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Dashboard device status refresh failed: {ex.Message}");
                        _deviceStatus = null;
                    }
                }
                else
                {
                    _deviceStatus = null;
                }
            }

            ApplyDeviceStatus();
        }
        catch (Exception ex)
        {
            _log.Warn($"Dashboard refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void FillRecent(IReadOnlyList<LedgerEntry>? entries = null)
    {
        entries ??= _ledger.All();
        var filter = _recentFilter.SelectedIndex;
        var shown = entries.Where(entry => filter switch
        {
            1 => entry.Status == LedgerStatus.NeedsReview,
            2 => entry.Status is LedgerStatus.Rejected or LedgerStatus.AmbiguousFailure ||
                 entry.PrintStatus is "Failed" or "ReprintFailed",
            3 => entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline,
            _ => true
        }).Take(50).ToList();

        _recent.BeginUpdate();
        _recent.Items.Clear();
        foreach (var entry in shown)
        {
            var item = new ListViewItem(entry.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
            {
                ForeColor = entry.Status switch
                {
                    LedgerStatus.Rejected or LedgerStatus.AmbiguousFailure => BadColor,
                    LedgerStatus.NeedsReview or LedgerStatus.InFlight => WarnColor,
                    _ => Color.FromArgb(15, 23, 42)
                }
            };
            item.SubItems.Add(entry.InvoiceNumber ?? "—");
            item.SubItems.Add(string.Equals(entry.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase) ? "Credit" : "Sale");
            item.SubItems.Add(entry.Status switch
            {
                LedgerStatus.InFlight => "In flight",
                LedgerStatus.Fiscalised => "Fiscalised",
                LedgerStatus.FiscalisedOffline => "Offline — queued",
                LedgerStatus.Rejected => "Rejected",
                LedgerStatus.NeedsReview => "Needs review",
                LedgerStatus.AmbiguousFailure => "Unknown outcome",
                _ => entry.Status.ToString()
            });
            item.SubItems.Add($"{entry.Currency} {entry.Total:0.00}");
            item.SubItems.Add(entry.PrintStatus);
            _recent.Items.Add(item);
        }

        _recent.EndUpdate();
        _recentCount.Text = $"{shown.Count} shown";
    }

    private void UpdateChecklist(IReadOnlyList<LedgerEntry> entries)
    {
        var pending = _sync.PendingCount + entries.Count(entry => entry.Status == LedgerStatus.InFlight);
        var review = entries.Count(entry => entry.Status == LedgerStatus.NeedsReview);

        SetCheck(_checkFdms, _deviceStatus is not null, _deviceStatus is null ? "Not reachable / not checked" : "Healthy");
        SetCheck(_checkQueue, pending == 0, pending == 0 ? "OK" : $"{pending} pending");
        SetCheck(_checkReview, review == 0, review == 0 ? "OK" : $"{review} files");
    }

    private static void SetCheck(Label label, bool ok, string text)
    {
        label.Text = (ok ? "✓ " : "✕ ") + text;
        label.ForeColor = ok ? OkColor : BadColor;
    }

    private void ApplyDeviceStatus()
    {
        var status = _deviceStatus;
        if (status is null)
        {
            _dayTitle.Text = "Fiscal day: unavailable";
            _dayMeta.Text = string.IsNullOrWhiteSpace(_config.ApiKey) ? "API key required" : "Could not read device status";
            _dayTimer.Text = "";
            _openDay.Enabled = false;
            _closeDay.Enabled = false;
            return;
        }

        var state = status.FiscalDayStatus switch
        {
            "FiscalDayOpened" => "open",
            "FiscalDayCloseFailed" => "open — last close failed",
            "FiscalDayCloseInitiated" => "closing…",
            "FiscalDayClosed" => "closed",
            _ => string.IsNullOrWhiteSpace(status.FiscalDayStatus) ? "unknown" : status.FiscalDayStatus
        };

        _dayTitle.Text = status.FiscalDayNo is int day ? $"Day {day}: {state}" : $"Fiscal day: {state}";
        _dayMeta.Text = status.IsClosePending && status.FiscalDayClosingErrorCode is not null
            ? $"Close error: {status.FiscalDayClosingErrorCode} — {FiscalStatusForm.DescribeCloseError(status.FiscalDayClosingErrorCode)}"
            : status.FiscalDayOpenedAt is { } opened
                ? $"Opened {opened.LocalDateTime:yyyy-MM-dd HH:mm}"
                : status.DeviceId.Length > 0 ? $"Device {status.DeviceId}" : "";

        _openDay.Enabled = !_dayActionInFlight && !status.IsDayOpen;
        _closeDay.Enabled = !_dayActionInFlight && status.IsDayOpen;
        _closeDay.Text = status.IsClosePending ? "Retry close fiscal day…" : "Close fiscal day…";
        UpdateDayTimer();
    }

    private void UpdateDayTimer()
    {
        if (_deviceStatus is { IsDayOpen: true, FiscalDayOpenedAt: { } opened })
        {
            var elapsed = DateTimeOffset.Now - opened;
            _dayTimer.Text = elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}"
                : elapsed.ToString(@"hh\:mm\:ss");
        }
        else
        {
            _dayTimer.Text = "";
        }
    }

    private async Task OpenDayAsync()
    {
        if (_deviceStatus is not { IsDayOpen: false })
        {
            return;
        }

        var answer = MessageBox.Show(
            "Open a new fiscal day?\n\nThe previous day must already be closed.",
            "Open fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _dayActionInFlight = true;
        ApplyDeviceStatus();
        try
        {
            var result = await _client.OpenFiscalDayAsync(_cts.Token);
            MessageBox.Show(result.Message, result.Success ? "Fiscal day opened" : "Could not open the fiscal day",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open the fiscal day", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _dayActionInFlight = false;
        }

        await RefreshAsync(forceDevice: true);
    }

    private async Task CloseDayAsync()
    {
        if (_deviceStatus is not { IsDayOpen: true })
        {
            return;
        }

        var entries = _ledger.All();
        var pending = _sync.PendingCount + entries.Count(entry => entry.Status == LedgerStatus.InFlight);
        var review = entries.Count(entry => entry.Status == LedgerStatus.NeedsReview);
        if (pending > 0 || review > 0)
        {
            MessageBox.Show(
                $"The day is not ready to close:\n\n" +
                $"• {pending} receipt(s) still queued or in flight\n" +
                $"• {review} receipt(s) needing review\n\n" +
                "Settle those first; the gateway rejects a close with outstanding documents.",
                "Close-day checks failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            "Close the fiscal day?\n\nEvery receipt for the day must be fiscalised. Closure can take up to a minute.",
            "Close fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _dayActionInFlight = true;
        ApplyDeviceStatus();
        try
        {
            var result = await _client.CloseFiscalDayAsync(_cts.Token);
            var detail = result.ErrorCode is null
                ? result.Message
                : $"{result.Message}\n\nGateway error: {result.ErrorCode} — {FiscalStatusForm.DescribeCloseError(result.ErrorCode)}";
            MessageBox.Show(detail,
                result.Success ? "Fiscal day closed" : result.Pending ? "Closure not confirmed yet" : "Could not close the fiscal day",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : result.Pending ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not close the fiscal day", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _dayActionInFlight = false;
        }

        await RefreshAsync(forceDevice: true);
    }

    private void OpenJobs()
    {
        // The jobs window owns reprint and per-job detail; the dashboard stays a read-only summary.
        using var form = new JobsForm(_ledger, _snapshots, _pipeline, _log);
        form.ShowDialog(this);
        _ = RefreshAsync();
    }

    /// <summary>Dependency-free 24-bar activity chart, drawn the same way FiscalBridge does it.</summary>
    private sealed class HourlyChart : Panel
    {
        private int[] _counts = new int[24];

        public HourlyChart()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        public void SetCounts(int[] counts)
        {
            _counts = counts;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var max = Math.Max(1, _counts.Max());
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            var bottom = height - 18;
            var barWidth = Math.Max(2, (width - 24) / 24 - 3);

            using var barBrush = new SolidBrush(Color.FromArgb(11, 122, 90));
            using var axisPen = new Pen(Color.FromArgb(226, 232, 240));
            using var labelBrush = new SolidBrush(MutedColor);
            using var labelFont = new Font("Segoe UI", 7.5f);

            graphics.DrawLine(axisPen, 8, bottom, width - 8, bottom);

            for (var hour = 0; hour < 24; hour++)
            {
                var x = 12 + hour * ((width - 24) / 24);
                var count = _counts[hour];
                var barHeight = count == 0 ? 0 : Math.Max(3, (int)((bottom - 10) * (count / (double)max)));
                if (barHeight > 0)
                {
                    graphics.FillRectangle(barBrush, x, bottom - barHeight, barWidth, barHeight);
                }

                if (hour % 3 == 0)
                {
                    graphics.DrawString(hour.ToString("00"), labelFont, labelBrush, x, bottom + 2);
                }
            }
        }
    }
}
