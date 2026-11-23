using System.Drawing;
using System.Globalization;
using System.Text;
using PrintInterceptor.Core.Reporting;
using PrintInterceptor.Core.State;

namespace PrintInterceptor;

/// <summary>
/// Report centre: a readable table of every fiscal line, the original Z-shift statement and the
/// accountant's exports. It reads local state only, so it works when the network is down, and no
/// action here can change a fiscal document. The date range is a read-only view over daily reports;
/// exports keep each line's own business date and fiscal proof.
/// </summary>
public sealed class DayReportForm : Form
{
    private const int MaxRangeDays = 31;

    private readonly FiscalDayReportService _reports;
    private readonly string _dataDirectory;

    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;
    private readonly DataGridView _lines;
    private readonly TextBox _statement;
    private readonly Label _rangeText;
    private readonly Label _kpiDocuments;
    private readonly Label _kpiGross;
    private readonly Label _kpiVat;
    private readonly Label _kpiAttention;
    private readonly Button _downloadPdf;
    private readonly Button _printReport;

    private FiscalDayReport? _report;
    private bool _embeddedMode;

    public bool EmbeddedMode
    {
        get => _embeddedMode;
        set
        {
            _embeddedMode = value;
            if (!value)
            {
                return;
            }

            FormBorderStyle = FormBorderStyle.None;
            TopMost = false;
            TopLevel = false;
            Dock = DockStyle.Fill;
            ShowInTaskbar = false;
        }
    }

    public event EventHandler? CloseRequested;

    public DayReportForm(FiscalDayReportService reports, string dataDirectory)
    {
        _reports = reports;
        _dataDirectory = dataDirectory;

        Text = "Report centre";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);
        ClientSize = new Size(1180, 720);
        MinimumSize = new Size(900, 560);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(0, 0, 0, 8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(20, 12, 20, 8) };
        var title = UiTheme.Heading("Report centre");
        title.Dock = DockStyle.Top;
        var subtitle = UiTheme.Subheading(
            "Fiscal lines, Z-shift totals and the accountant's exports — read only, and available offline.");
        subtitle.Dock = DockStyle.Top;
        subtitle.Padding = new Padding(0, 4, 0, 0);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        _from = DatePicker(DateTime.Today);
        _to = DatePicker(DateTime.Today);
        _from.ValueChanged += (_, _) => RefreshReport();
        _to.ValueChanged += (_, _) => RefreshReport();
        _rangeText = UiTheme.Subheading("");
        _rangeText.Margin = new Padding(14, 9, 3, 3);

        var today = ShortButton("Today");
        today.Click += (_, _) => SetRange(DateTime.Today, DateTime.Today);
        var week = ShortButton("Last 7 days");
        week.Click += (_, _) => SetRange(DateTime.Today.AddDays(-6), DateTime.Today);
        var refresh = ShortButton("Refresh");
        refresh.Click += (_, _) => RefreshReport();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 8, 20, 6),
            WrapContents = false
        };
        toolbar.Controls.Add(UiTheme.SectionLabel("From"));
        toolbar.Controls.Add(_from);
        toolbar.Controls.Add(UiTheme.SectionLabel("To"));
        toolbar.Controls.Add(_to);
        toolbar.Controls.Add(today);
        toolbar.Controls.Add(week);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_rangeText);

        _kpiDocuments = KpiValue();
        _kpiGross = KpiValue();
        _kpiVat = KpiValue();
        _kpiAttention = KpiValue();

        var kpis = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(14, 4, 14, 4)
        };
        for (var column = 0; column < 4; column++)
        {
            kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        }

        kpis.Controls.Add(KpiCard("Documents issued", _kpiDocuments), 0, 0);
        kpis.Controls.Add(KpiCard("Net position", _kpiGross), 1, 0);
        kpis.Controls.Add(KpiCard("VAT position", _kpiVat), 2, 0);
        kpis.Controls.Add(KpiCard("Needs attention", _kpiAttention), 3, 0);

        _lines = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToResizeRows = false
        };
        UiTheme.StyleGrid(_lines);
        BuildColumns();

        _statement = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Mono(9f),
            Padding = new Padding(8)
        };

        var tabs = new TabControl { Dock = DockStyle.Fill, Font = UiTheme.Ui(9f) };
        var linesPage = new TabPage("Fiscal lines") { BackColor = UiTheme.Surface, Padding = new Padding(6) };
        linesPage.Controls.Add(_lines);
        var statementPage = new TabPage("Z-shift statement") { BackColor = UiTheme.Surface, Padding = new Padding(6) };
        statementPage.Controls.Add(_statement);
        tabs.TabPages.Add(linesPage);
        tabs.TabPages.Add(statementPage);

        var documentHost = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(14, 4, 14, 4),
            Padding = new Padding(8)
        };
        documentHost.Controls.Add(tabs);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 10, 20, 8),
            WrapContents = false
        };

        var close = new Button { Text = "Close", Width = 88, DialogResult = DialogResult.Cancel };
        UiTheme.Secondary(close);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _printReport = new Button { Text = "Print report", Width = 116 };
        UiTheme.Secondary(_printReport);
        _printReport.Click += (_, _) => PrintReport();

        _downloadPdf = new Button { Text = "Download PDF", Width = 130 };
        UiTheme.Primary(_downloadPdf);
        _downloadPdf.Click += (_, _) => DownloadPdf();

        var csv = new Button { Text = "Export CSV", Width = 112 };
        UiTheme.Secondary(csv);
        csv.Click += (_, _) => ExportLines();

        var summary = new Button { Text = "Save TXT", Width = 100 };
        UiTheme.Secondary(summary);
        summary.Click += (_, _) => SaveSummary();

        actions.Controls.Add(close);
        actions.Controls.Add(_printReport);
        actions.Controls.Add(_downloadPdf);
        actions.Controls.Add(csv);
        actions.Controls.Add(summary);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(kpis, 0, 2);
        root.Controls.Add(documentHost, 0, 3);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        CancelButton = close;

        RefreshReport();
    }

    private static DateTimePicker DatePicker(DateTime value) => new()
    {
        Format = DateTimePickerFormat.Short,
        Width = 128,
        Value = value,
        Font = UiTheme.Ui(9f)
    };

    private static Button ShortButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true };
        UiTheme.Secondary(button);
        return button;
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
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(4, 2, 4, 2) };
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
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Ui(8.5f),
            Margin = new Padding(2, 2, 3, 0)
        });
        stack.Controls.Add(value);
        card.Controls.Add(stack);
        return card;
    }

    private void BuildColumns()
    {
        _lines.Columns.Add(Column("Business date", 96));
        _lines.Columns.Add(Column("Invoice", 126));
        _lines.Columns.Add(Column("Type", 78));
        _lines.Columns.Add(Column("Item", 210));
        _lines.Columns.Add(Column("Qty", 52, DataGridViewContentAlignment.MiddleRight));
        _lines.Columns.Add(Column("Total", 92, DataGridViewContentAlignment.MiddleRight));
        _lines.Columns.Add(Column("Tax type", 108));
        _lines.Columns.Add(Column("Rate", 58, DataGridViewContentAlignment.MiddleRight));
        _lines.Columns.Add(Column("Fiscal code", 150));
        _lines.Columns.Add(Column("Receipt", 86));
    }

    private static DataGridViewTextBoxColumn Column(
        string title,
        int width,
        DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft) => new()
    {
        HeaderText = title,
        Width = width,
        SortMode = DataGridViewColumnSortMode.Automatic,
        DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
    };

    private void SetRange(DateTime from, DateTime to)
    {
        _from.Value = from.Date;
        _to.Value = to.Date;
        RefreshReport();
    }

    private void RefreshReport()
    {
        try
        {
            var from = DateOnly.FromDateTime(_from.Value.Date);
            var to = DateOnly.FromDateTime(_to.Value.Date);
            if (to < from)
            {
                _rangeText.Text = "The end date is before the start date.";
                _rangeText.ForeColor = UiTheme.Danger;
                _report = null;
                _lines.Rows.Clear();
                _statement.Text = "";
                SetKpis("-", "-", "-", "-");
                return;
            }

            if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            {
                _rangeText.Text = $"Choose at most {MaxRangeDays} days.";
                _rangeText.ForeColor = UiTheme.Danger;
                _report = null;
                _lines.Rows.Clear();
                _statement.Text = "";
                SetKpis("-", "-", "-", "-");
                return;
            }

            _report = BuildReport(from, to);
            _rangeText.Text = from == to
                ? from.ToString("dddd, d MMMM yyyy")
                : $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}";
            _rangeText.ForeColor = UiTheme.Muted;

            _lines.SuspendLayout();
            _lines.Rows.Clear();
            foreach (var line in _report.Lines)
            {
                _lines.Rows.Add(
                    line.BusinessDate,
                    line.InvoiceNumber,
                    TypeText(line.TransactionType),
                    line.Line,
                    line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                    $"{line.Currency} {line.LineTotal:0.00}".Trim(),
                    line.TaxType,
                    line.TaxRatePercent is { } rate ? $"{rate:0.###}%" : "-",
                    line.FiscalCode ?? "-",
                    line.ReceiptNumber?.ToString() ?? "-");
            }
            _lines.ResumeLayout();

            _statement.Text = _report.Render();
            var attention = _report.HeldForReview + _report.Rejected + _report.UnknownOutcome +
                            _report.TotalMismatchDocuments + _report.WithoutItemDetail.Count;
            SetKpis(
                _report.Documents.ToString(CultureInfo.InvariantCulture),
                Money(_report.Gross),
                Money(_report.Tax),
                attention.ToString(CultureInfo.InvariantCulture));

            _downloadPdf.Enabled = true;
            _printReport.Enabled = true;
        }
        catch (Exception ex)
        {
            _report = null;
            _lines.Rows.Clear();
            _statement.Text = "The report could not be read:" + Environment.NewLine + ex.Message;
            _rangeText.Text = "Could not read this range.";
            _rangeText.ForeColor = UiTheme.Danger;
            SetKpis("-", "-", "-", "-");
            _downloadPdf.Enabled = false;
            _printReport.Enabled = false;
        }
    }

    private void SetKpis(string documents, string gross, string vat, string attention)
    {
        _kpiDocuments.Text = documents;
        _kpiGross.Text = gross;
        _kpiVat.Text = vat;
        _kpiAttention.Text = attention;
        _kpiAttention.ForeColor = attention is "-" or "0" ? UiTheme.Text : UiTheme.Warning;
    }

    /// <summary>Combines daily reports for a short range without recalculating any fiscal value.</summary>
    private FiscalDayReport BuildReport(DateOnly from, DateOnly to)
    {
        var report = _reports.Build(from);
        if (from == to)
        {
            return report;
        }

        var days = new List<FiscalDayReport> { report };
        for (var date = from.AddDays(1); date <= to; date = date.AddDays(1))
        {
            days.Add(_reports.Build(date));
        }

        return new FiscalDayReport
        {
            BusinessDate = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}",
            CompanyName = report.CompanyName,
            CompanyTin = report.CompanyTin,
            CompanyVatNumber = report.CompanyVatNumber,
            DeviceId = report.DeviceId,
            DeviceSerialNo = report.DeviceSerialNo,
            FirstReceiptNumber = days.Select(day => day.FirstReceiptNumber).FirstOrDefault(value => value.HasValue),
            LastReceiptNumber = days.Select(day => day.LastReceiptNumber).LastOrDefault(value => value.HasValue),
            FirstCounter = days.Select(day => day.FirstCounter).FirstOrDefault(value => value.HasValue),
            LastCounter = days.Select(day => day.LastCounter).LastOrDefault(value => value.HasValue),
            Sales = CombineKind(days.Select(day => day.Sales)),
            Credits = CombineKind(days.Select(day => day.Credits)),
            Debits = CombineKind(days.Select(day => day.Debits)),
            WaitingForSync = days.Sum(day => day.WaitingForSync),
            HeldForReview = days.Sum(day => day.HeldForReview),
            Rejected = days.Sum(day => day.Rejected),
            UnknownOutcome = days.Sum(day => day.UnknownOutcome),
            InFlight = days.Sum(day => day.InFlight),
            WithoutItemDetail = days.SelectMany(day => day.WithoutItemDetail).ToList(),
            Lines = days.SelectMany(day => day.Lines)
                .OrderBy(line => line.BusinessDate, StringComparer.Ordinal)
                .ThenBy(line => line.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Currencies = days.SelectMany(day => day.Currencies)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static FiscalDayKind CombineKind(IEnumerable<FiscalDayKind> kinds)
    {
        var list = kinds.ToList();
        var buckets = list.SelectMany(kind => kind.Buckets)
            .GroupBy(bucket => bucket.Label, StringComparer.Ordinal)
            .Select(group => new FiscalDayBucket(
                group.Key,
                group.Sum(bucket => bucket.Documents),
                group.Sum(bucket => bucket.Net),
                group.Sum(bucket => bucket.Tax),
                group.Sum(bucket => bucket.Gross)))
            .ToList();

        return new FiscalDayKind
        {
            Documents = list.Sum(kind => kind.Documents),
            WithItemDetail = list.Sum(kind => kind.WithItemDetail),
            Net = list.Sum(kind => kind.Net),
            Tax = list.Sum(kind => kind.Tax),
            Gross = list.Sum(kind => kind.Gross),
            GrossWithoutItemDetail = list.Sum(kind => kind.GrossWithoutItemDetail),
            TotalMismatch = list.Sum(kind => kind.TotalMismatch),
            Buckets = buckets
        };
    }

    private void SaveSummary()
    {
        if (_report is not { } report)
        {
            return;
        }

        if (Save(
                $"fiscal-day-{FilePart(report.BusinessDate)}.txt",
                "Day report (*.txt)|*.txt",
                report.Render(),
                new UTF8Encoding(false)) is not { } path)
        {
            return;
        }

        MessageBox.Show(this,
            $"The summary was written to:{Environment.NewLine}{path}",
            "Day report saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// The accountant's file: one row per printed line with the fiscal proof on it. Written with a
    /// byte-order mark so Excel reads a product name typed in any language correctly.
    /// </summary>
    private void ExportLines()
    {
        if (_report is not { } report)
        {
            return;
        }

        if (report.Lines.Count == 0)
        {
            MessageBox.Show(this,
                $"No fiscalised line was printed for {report.BusinessDate}, so there is nothing to export.",
                "Nothing to export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (Save(
                $"fiscal-lines-{FilePart(report.BusinessDate)}.csv",
                "CSV file (*.csv)|*.csv",
                report.Csv(),
                new UTF8Encoding(true)) is not { } path)
        {
            return;
        }

        MessageBox.Show(this,
            $"{report.Lines.Count} line(s) were written to:{Environment.NewLine}{path}",
            "Export written", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DownloadPdf()
    {
        if (_report is not { } report)
        {
            return;
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);

        var baseName = $"FiscalStack-report-{FilePart(report.BusinessDate)}";
        var target = Path.Combine(downloads, baseName + ".pdf");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(downloads, $"{baseName} ({i}).pdf");
        }

        try
        {
            ReportDocument.Print(report, target);
            MessageBox.Show(this,
                $"The report was saved to:{Environment.NewLine}{target}",
                "PDF report saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save the PDF report",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PrintReport()
    {
        if (_report is not { } report)
        {
            return;
        }

        try
        {
            ReportDocument.Print(report);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not print the report",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string? Save(string fileName, string filter, string contents, Encoding encoding)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save report",
            Filter = filter,
            FileName = fileName,
            InitialDirectory = Directory.Exists(_dataDirectory) ? _dataDirectory : null
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        try
        {
            File.WriteAllText(dialog.FileName, contents, encoding);
            return dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"The file could not be written: {ex.Message}",
                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    private static string TypeText(string type) => type switch
    {
        "CreditNote" => "Credit note",
        "DebitNote" => "Debit note",
        _ => "Sale"
    };

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FilePart(string value)
    {
        var text = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrWhiteSpace(text) ? "report" : text;
    }
}
