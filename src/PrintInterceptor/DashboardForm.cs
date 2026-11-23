using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PrintInterceptor.Core.Capture;
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
public sealed record DashboardActions(
    Func<SettingsForm> CreateSettingsForm,
    Action<string> RunTool,
    Func<JobsForm>? CreateJobsForm = null,
    Func<DayReportForm>? CreateReportForm = null);

/// <summary>
/// Operator console: the dashboard is the single place to work, and the tray is deliberately
/// short. Pages are switched in the left rail; the old dialogs stay available as pages or as
/// tool actions from the same rail.
/// </summary>
public sealed class DashboardForm : Form
{
    private const int RecentUpdated = 0;
    private const int RecentInvoice = 1;
    private const int RecentType = 2;
    private const int RecentStatus = 3;
    private const int RecentTotal = 4;
    private const int RecentPrint = 5;

    private static readonly Color OkColor = UiTheme.Success;
    private static readonly Color WarnColor = UiTheme.Warning;
    private static readonly Color BadColor = UiTheme.Danger;
    private static readonly Color MutedColor = UiTheme.Muted;

    private readonly InterceptorConfig _config;
    private readonly FiscalStackClient _client;
    private readonly CompanyProfileService _company;
    private readonly JobLedger _ledger;
    private readonly OfflineSyncEngine _sync;
    private readonly PrintSnapshotStore _snapshots;
    private readonly PrintJobPipeline _pipeline;
    private readonly JobStore _jobs;
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
    private readonly DataGridView _recent;
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

    private readonly DashboardActions _actions;
    private readonly PrintJobServer _server;
    private readonly Panel _pages;
    private readonly Dictionary<string, Control> _pageCache = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Button _pauseButton;
    private readonly Label _captureState;
    private string _currentPage = "Overview";

    public DashboardForm(
        InterceptorConfig config,
        FiscalStackClient client,
        CompanyProfileService company,
        JobLedger ledger,
        OfflineSyncEngine sync,
        PrintSnapshotStore snapshots,
        PrintJobPipeline pipeline,
        JobStore jobs,
        ILog log,
        DashboardActions actions,
        PrintJobServer server,
        string? initialPage = null)
    {
        _config = config;
        _client = client;
        _company = company;
        _ledger = ledger;
        _sync = sync;
        _snapshots = snapshots;
        _pipeline = pipeline;
        _jobs = jobs;
        _log = log;
        _actions = actions;
        _server = server;

        Text = "PrintInterceptor — Operator console";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 760);
        MinimumSize = new Size(1060, 640);
        ShowInTaskbar = true;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);

        _pages = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Canvas };
        _captureState = new Label
        {
            AutoSize = true,
            Font = UiTheme.Ui(8.5f),
            ForeColor = Color.FromArgb(203, 213, 225),
            Margin = new Padding(14, 6, 10, 2)
        };
        _pauseButton = new Button
        {
            Height = 34,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.NavySoft,
            ForeColor = Color.White,
            Font = UiTheme.Ui(9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 8, 0)
        };
        _pauseButton.FlatAppearance.BorderSize = 0;
        _pauseButton.Click += (_, _) => ToggleCapture();

        _identity = new Label
        {
            AutoSize = true,
            Font = UiTheme.Ui(12f, FontStyle.Bold),
            ForeColor = UiTheme.Text,
            Margin = new Padding(12, 10, 3, 3)
        };

        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(92, 30) };
        UiTheme.Secondary(refresh);
        refresh.Click += (_, _) => _ = RefreshAsync(forceDevice: true);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Surface,
            Padding = new Padding(14, 8, 14, 4)
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

        _chart = new HourlyChart { Dock = DockStyle.Fill };

        var chartHost = new CardPanel
        {
            Dock = DockStyle.Top,
            Height = 166,
            Margin = new Padding(14, 4, 14, 4),
            Padding = new Padding(14, 10, 14, 10)
        };
        var chartTitle = UiTheme.Heading("Fiscalised receipts by hour (today)");
        chartTitle.Dock = DockStyle.Top;
        chartTitle.Font = UiTheme.Ui(10.5f, FontStyle.Bold);
        chartHost.Controls.Add(_chart);
        chartHost.Controls.Add(chartTitle);

        _recentFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 170,
            Font = UiTheme.Ui(9f)
        };
        _recentFilter.Items.AddRange(new object[] { "All jobs", "Needs review", "Failures", "Fiscalised" });
        _recentFilter.SelectedIndex = 0;
        _recentFilter.SelectedIndexChanged += (_, _) => FillRecent();

        _recentCount = UiTheme.Subheading("");
        _recentCount.Margin = new Padding(14, 9, 3, 3);

        var openWorklist = new Button { Text = "Open worklist →", AutoSize = true };
        UiTheme.Ghost(openWorklist);
        openWorklist.Click += (_, _) => OpenJobs();

        var recentToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Surface,
            Padding = new Padding(12, 6, 12, 2)
        };
        recentToolbar.Controls.Add(UiTheme.SectionLabel("Show"));
        recentToolbar.Controls.Add(_recentFilter);
        recentToolbar.Controls.Add(_recentCount);
        recentToolbar.Controls.Add(openWorklist);

        _recent = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToResizeRows = false
        };
        UiTheme.StyleGrid(_recent);
        _recent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Updated", Width = 128 });
        _recent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Invoice", Width = 150 });
        _recent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Width = 78 });
        _recent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Width = 124 });
        _recent.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Total",
            Width = 104,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _recent.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Print", Width = 104 });
        _recent.CellDoubleClick += (_, _) => OpenJobs();
        _recent.CellPainting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == RecentStatus && e.Graphics is { } graphics &&
                e.Value is LedgerStatus status)
            {
                e.PaintBackground(e.CellBounds, true);
                var pill = UiTheme.Status(status);
                UiTheme.PaintPill(graphics, e.CellBounds, pill.Text, pill.Fore, pill.Back);
                e.Handled = true;
            }
        };

        var recentHost = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(14, 4, 14, 8),
            Padding = new Padding(8)
        };
        var recentTitle = UiTheme.Heading("Recent fiscalisation jobs");
        recentTitle.Dock = DockStyle.Top;
        recentTitle.Font = UiTheme.Ui(10.5f, FontStyle.Bold);
        recentTitle.Padding = new Padding(6, 2, 0, 6);
        recentHost.Controls.Add(_recent);
        recentHost.Controls.Add(recentToolbar);
        recentHost.Controls.Add(recentTitle);

        _dayTitle = new Label
        {
            AutoSize = true,
            Font = UiTheme.Ui(10.5f, FontStyle.Bold),
            ForeColor = UiTheme.Text,
            Margin = new Padding(8, 6, 3, 2)
        };
        _dayMeta = new Label { AutoSize = true, ForeColor = MutedColor, Margin = new Padding(8, 2, 3, 2) };
        _dayTimer = new Label
        {
            AutoSize = true,
            Font = UiTheme.Mono(12f),
            ForeColor = UiTheme.Text,
            Margin = new Padding(8, 2, 3, 6)
        };

        var dayCard = new CardPanel
        {
            Dock = DockStyle.Top,
            Height = 126,
            Margin = new Padding(14, 4, 14, 4),
            Padding = new Padding(12, 10, 12, 8)
        };
        var dayHeading = UiTheme.Heading("Fiscal day");
        dayHeading.Dock = DockStyle.Top;
        dayHeading.Font = UiTheme.Ui(10.5f, FontStyle.Bold);
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
        dayCard.Controls.Add(dayHeading);

        _checkFdms = CheckLabel();
        _checkQueue = CheckLabel();
        _checkReview = CheckLabel();

        _openDay = new Button { Text = "Open fiscal day…", AutoSize = true, Enabled = false };
        UiTheme.Secondary(_openDay);
        _openDay.Click += (_, _) => _ = OpenDayAsync();
        _closeDay = new Button { Text = "Close fiscal day…", AutoSize = true, Enabled = false };
        UiTheme.Secondary(_closeDay);
        _closeDay.Click += (_, _) => _ = CloseDayAsync();

        var dayButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(6, 4, 6, 4)
        };
        dayButtons.Controls.Add(_closeDay);
        dayButtons.Controls.Add(_openDay);

        var checklist = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(14, 4, 14, 8),
            Padding = new Padding(12, 10, 12, 8)
        };
        var checkTitle = UiTheme.Heading("Close-day checks");
        checkTitle.Dock = DockStyle.Top;
        checkTitle.Font = UiTheme.Ui(10.5f, FontStyle.Bold);
        checkTitle.Margin = new Padding(0, 0, 0, 6);
        var checkStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(4)
        };
        checkStack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62f));
        checkStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        checkStack.Controls.Add(UiTheme.SectionLabel("FDMS"), 0, 0);
        checkStack.Controls.Add(_checkFdms, 1, 0);
        checkStack.Controls.Add(UiTheme.SectionLabel("Queue"), 0, 1);
        checkStack.Controls.Add(_checkQueue, 1, 1);
        checkStack.Controls.Add(UiTheme.SectionLabel("Review"), 0, 2);
        checkStack.Controls.Add(_checkReview, 1, 2);
        checklist.Controls.Add(checkStack);
        checklist.Controls.Add(dayButtons);
        checklist.Controls.Add(checkTitle);

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 140f));
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
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 180f));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        main.Controls.Add(chartHost, 0, 0);
        main.Controls.Add(content, 0, 1);

        // The overview carries the day's numbers and a compact worklist; deeper work happens on
        // the Jobs and Reports pages in the same shell.
        var overview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Canvas
        };
        overview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overview.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
        overview.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        overview.Controls.Add(main, 0, 2);
        overview.Controls.Add(kpis, 0, 1);
        overview.Controls.Add(header, 0, 0);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214f));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        shell.Controls.Add(_pages, 1, 0);
        shell.Controls.Add(BuildSidebar(), 0, 0);
        Controls.Add(shell);

        _pageCache["Overview"] = overview;
        _pages.Controls.Add(overview);
        SelectPage(string.IsNullOrWhiteSpace(initialPage) ? "Overview" : initialPage!);

        _server.ProcessingPauseChanged += OnProcessingPauseChanged;
        UpdateCaptureState();

        _timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => UpdateDayTimer();
        _clock.Start();

        FormClosed += (_, _) =>
        {
            _server.ProcessingPauseChanged -= OnProcessingPauseChanged;
            _cts.Cancel();
            _timer.Stop();
            _timer.Dispose();
            _clock.Stop();
            _clock.Dispose();
            _cts.Dispose();
        };

        _ = RefreshAsync(forceDevice: true);
    }

    /// <summary>Used by the tray to open a specific page, including first-run Settings.</summary>
    public void NavigateTo(string page) => SelectPage(page);

    private Control BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 214,
            BackColor = UiTheme.Navy
        };

        var title = new Label
        {
            Text = "FiscalStack",
            Dock = DockStyle.Top,
            Height = 44,
            Font = UiTheme.Ui(14f, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 8, 0)
        };
        var subtitle = new Label
        {
            Text = "PrintInterceptor console",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.FromArgb(148, 163, 184),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 8, 0)
        };

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8, 10, 8, 8)
        };
        AddNav(nav, "Overview", "Overview");
        AddNav(nav, "Parse rules", "Parse rules");
        AddNav(nav, "Settings", "Settings");
        AddNav(nav, "Jobs", page: "Jobs");
        AddNav(nav, "Fiscal status", tool: "fiscal-status");
        AddNav(nav, "Reports", page: "Reports");
        AddNav(nav, "Capture layout", tool: "capture");
        AddNav(nav, "Files && tools", "Tools");

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 126 };
        var updates = new Button
        {
            Text = "Check for updates",
            Dock = DockStyle.Top,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            Font = UiTheme.Ui(9f),
            BackColor = UiTheme.NavySoft,
            ForeColor = Color.FromArgb(203, 213, 225),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 8, 0)
        };
        updates.FlatAppearance.BorderSize = 0;
        updates.Click += (_, _) => RunTool("updates");
        footer.Controls.Add(_pauseButton);
        footer.Controls.Add(updates);
        footer.Controls.Add(_captureState);

        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(subtitle);
        sidebar.Controls.Add(title);
        return sidebar;
    }

    private void AddNav(FlowLayoutPanel nav, string label, string? page = null, string? tool = null)
    {
        var button = new Button
        {
            Text = label,
            Width = 188,
            Margin = new Padding(0, 0, 0, 4)
        };
        UiTheme.NavButton(button, selected: false, dark: true);
        button.Click += (_, _) =>
        {
            if (page is not null)
            {
                SelectPage(page);
            }
            else if (tool is not null)
            {
                RunTool(tool);
            }
        };
        nav.Controls.Add(button);
        if (page is not null)
        {
            _navButtons[page] = button;
        }
    }

    private void SelectPage(string name)
    {
        if (!_pageCache.TryGetValue(name, out var page))
        {
            page = CreatePage(name);
            _pageCache[name] = page;
            _pages.Controls.Add(page);
        }

        foreach (Control existing in _pages.Controls)
        {
            existing.Visible = existing == page;
        }

        page.BringToFront();
        _currentPage = name;
        foreach (var (key, button) in _navButtons)
        {
            UiTheme.NavButton(button, selected: key == name, dark: true);
        }

        if (name == "Overview")
        {
            _ = RefreshAsync();
        }
    }

    private Control CreatePage(string name) => name switch
    {
        "Settings" => CreateSettingsPage(),
        "Parse rules" => new ParseRulesPage(_config, _pipeline, _log) { Dock = DockStyle.Fill },
        "Jobs" => CreateJobsPage(),
        "Reports" => CreateReportsPage(),
        "Tools" => CreateToolsPage(),
        _ => new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Canvas }
    };

    private Control CreateJobsPage()
    {
        var form = _actions.CreateJobsForm is { } factory
            ? factory()
            : new JobsForm(_ledger, _snapshots, _pipeline, _jobs, _log);
        form.EmbeddedMode = true;
        form.CloseRequested += (_, _) => SelectPage("Overview");
        return form;
    }

    private Control CreateReportsPage()
    {
        if (_actions.CreateReportForm is not { } factory)
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Canvas };
        }

        var form = factory();
        form.EmbeddedMode = true;
        form.CloseRequested += (_, _) => SelectPage("Overview");
        return form;
    }

    private Control CreateSettingsPage()
    {
        var settings = _actions.CreateSettingsForm();
        settings.EmbeddedMode = true;
        settings.SettingsSaved += (_, _) => _ = RefreshAsync();
        settings.CloseRequested += (_, _) => SelectPage("Overview");
        return settings;
    }

    private Control CreateToolsPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Canvas,
            AutoScroll = true,
            Padding = new Padding(24, 20, 24, 20)
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        void AddRow(Control control)
        {
            stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 0, 0, 14);
            stack.Controls.Add(control, 0, stack.RowCount - 1);
        }

        var header = new Panel { AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        var title = UiTheme.Heading("Files && tools");
        title.Dock = DockStyle.Top;
        var subtitle = UiTheme.Subheading(
            "Open the files and helpers behind this till. Nothing here changes fiscal state.");
        subtitle.Dock = DockStyle.Top;
        subtitle.Padding = new Padding(0, 4, 0, 0);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        AddRow(header);

        void Card(string heading, params (string Text, string Id)[] actions)
        {
            var card = new CardPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0) };
            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(18, 14, 18, 16)
            };

            var label = UiTheme.SectionLabel(heading.ToUpperInvariant());
            label.Font = UiTheme.Ui(9.5f, FontStyle.Bold);
            label.Margin = new Padding(0, 0, 0, 10);
            body.Controls.Add(label);

            foreach (var (text, id) in actions)
            {
                var button = new Button
                {
                    Text = text,
                    AutoSize = true,
                    MinimumSize = new Size(360, 36),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 0, 0, 6)
                };
                UiTheme.Secondary(button);
                button.Padding = new Padding(14, 0, 12, 0);
                button.Click += (_, _) => RunTool(id);
                body.Controls.Add(button);
            }

            card.Controls.Add(body);
            AddRow(card);
        }

        Card("FiscalStack",
            ("Sync offline receipts now", "sync"),
            ("Reconcile unknown receipts now", "reconcile"),
            ("Test connection", "test-connection"));
        Card("Receipt parsing",
            ("Import receipt parse profile…", "import-profile"),
            ("Open parse profiles folder", "profiles"),
            ("Capture receipt layout from a job…", "capture"));
        Card("Files",
            ("Open configuration file", "open-config"),
            ("Open data folder", "data"),
            ("Open received files folder", "received"),
            ("Open fiscalised invoices folder", "fiscalised"),
            ("Open logs folder", "logs"),
            ("Open current log file", "current-log"));
        Card("Windows",
            ("Start with Windows", "autostart"));

        page.Controls.Add(stack);
        return page;
    }

    private void RunTool(string id)
    {
        _actions.RunTool(id);
        if (_currentPage == "Overview")
        {
            _ = RefreshAsync();
        }
    }

    private void ToggleCapture()
    {
        RunTool("toggle-capture");
        UpdateCaptureState();
    }

    private void OnProcessingPauseChanged(object? sender, EventArgs e) => UpdateCaptureState();

    private void UpdateCaptureState()
    {
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(UpdateCaptureState));
            return;
        }

        var paused = _server.IsProcessingPaused;
        _captureState.Text = paused ? "Capture: PAUSED — jobs are queued" : "Capture: running";
        _captureState.ForeColor = paused ? Color.FromArgb(253, 224, 71) : Color.FromArgb(167, 243, 208);
        _pauseButton.Text = paused ? "Resume capture" : "Pause capture";
    }

    private static Label KpiValue() => new()
    {
        AutoSize = true,
        Font = UiTheme.Ui(15f, FontStyle.Bold),
        ForeColor = UiTheme.Text,
        Margin = new Padding(10, 2, 3, 6)
    };

    private static CardPanel KpiCard(string title, Label value)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 4, 2),
            Padding = new Padding(10, 8, 10, 6)
        };

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };
        var label = UiTheme.SectionLabel(title);
        label.Margin = new Padding(0, 0, 3, 2);
        stack.Controls.Add(label);
        stack.Controls.Add(value);
        card.Controls.Add(stack);
        return card;
    }

    private static Label CheckLabel() => new()
    {
        AutoSize = true,
        Font = UiTheme.Ui(9f),
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
            _kpiTotal.Font = UiTheme.Ui(totals.Any() && _kpiTotal.Text.Length > 22 ? 10.5f : 15f, FontStyle.Bold);

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

        _recent.SuspendLayout();
        _recent.Rows.Clear();
        foreach (var entry in shown)
        {
            var index = _recent.Rows.Add(
                entry.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                entry.InvoiceNumber ?? "—",
                string.Equals(entry.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase) ? "Credit" : "Sale",
                entry.Status,
                $"{entry.Currency} {entry.Total:0.00}".Trim(),
                entry.PrintStatus);
            _recent.Rows[index].Tag = entry;
        }

        _recent.ResumeLayout();
        _recentCount.Text = $"{shown.Count} of {entries.Count}";
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
        // The worklist is now a page in the same console, not a second window.
        SelectPage("Jobs");
        _ = RefreshAsync();
    }

    /// <summary>Dependency-free 24-bar activity chart, drawn the same way FiscalBridge does it.</summary>
    private sealed class HourlyChart : Panel
    {
        private int[] _counts = new int[24];

        public HourlyChart()
        {
            DoubleBuffered = true;
            BackColor = UiTheme.Surface;
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

            using var barBrush = new SolidBrush(UiTheme.Success);
            using var axisPen = new Pen(UiTheme.Border);
            using var labelBrush = new SolidBrush(MutedColor);
            using var labelFont = UiTheme.Ui(7.5f);

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
