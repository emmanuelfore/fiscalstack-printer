using System.Text;
using PrintInterceptor.Core.Reporting;

namespace PrintInterceptor;

/// <summary>
/// The day's fiscal position as the till knows it, with the accountant's export beside it. The
/// report reads local state only, so it is available when the network is not, and nothing in this
/// window can change a fiscal document: it is a statement of what was already issued.
/// </summary>
public sealed class DayReportForm : Form
{
    private readonly FiscalDayReportService _reports;
    private readonly string _dataDirectory;
    private readonly DateTimePicker _date;
    private readonly TextBox _text;

    private FiscalDayReport? _report;

    public DayReportForm(FiscalDayReportService reports, string dataDirectory)
    {
        _reports = reports;
        _dataDirectory = dataDirectory;

        Text = "Daily Z-shift summary";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(680, 460);

        _date = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Width = 120,
            Value = DateTime.Today
        };
        _date.ValueChanged += (_, _) => RefreshReport();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 4)
        };
        toolbar.Controls.Add(new Label { Text = "Business date:", AutoSize = true, Margin = new Padding(3, 8, 6, 3) });
        toolbar.Controls.Add(_date);
        toolbar.Controls.Add(DayButton("Previous day", -1));
        toolbar.Controls.Add(DayButton("Next day", 1));

        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 250, 252),
            Font = new Font("Consolas", 9.75f),
            BorderStyle = BorderStyle.FixedSingle
        };

        var save = new Button { Text = "Save summary…", Width = 130 };
        save.Click += (_, _) => SaveSummary();
        var csv = new Button { Text = "Export lines (CSV)…", Width = 160 };
        csv.Click += (_, _) => ExportLines();
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(10)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(csv);
        buttons.Controls.Add(save);

        Controls.Add(_text);
        Controls.Add(buttons);
        Controls.Add(toolbar);

        CancelButton = close;
        RefreshReport();
    }

    private Button DayButton(string text, int days)
    {
        var button = new Button { Text = text, Width = 110 };
        button.Click += (_, _) => _date.Value = _date.Value.Date.AddDays(days);
        return button;
    }

    private void RefreshReport()
    {
        try
        {
            _report = _reports.Build(DateOnly.FromDateTime(_date.Value.Date));
            _text.Text = _report.Render();
        }
        catch (Exception ex)
        {
            _report = null;
            _text.Text = "The day report could not be read:\r\n" + ex.Message;
        }
    }

    private void SaveSummary()
    {
        if (_report is not { } report)
        {
            return;
        }

        if (Save(
                $"fiscal-day-{report.BusinessDate}.txt",
                "Day report (*.txt)|*.txt",
                report.Render(),
                new UTF8Encoding(false)) is not { } path)
        {
            return;
        }

        MessageBox.Show(
            this,
            $"The summary was written to:\r\n{path}",
            "Day report saved",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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
            MessageBox.Show(
                this,
                $"No fiscalised line was printed on {report.BusinessDate}, so there is nothing to export.",
                "Nothing to export",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (Save(
                $"fiscal-lines-{report.BusinessDate}.csv",
                "CSV file (*.csv)|*.csv",
                report.Csv(),
                new UTF8Encoding(true)) is not { } path)
        {
            return;
        }

        MessageBox.Show(
            this,
            $"{report.Lines.Count} line(s) were written to:\r\n{path}",
            "Export written",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private string? Save(string fileName, string filter, string contents, Encoding encoding)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save for the day",
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
            MessageBox.Show(
                this,
                $"The file could not be written: {ex.Message}",
                "Save failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return null;
        }
    }
}
