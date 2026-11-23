using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using PrintInterceptor.Core.Reporting;

namespace PrintInterceptor;

/// <summary>
/// Prints the Z-shift report as a clean table, or writes the same document to a PDF with the
/// Windows Microsoft Print to PDF driver. It only reads the report model; no fiscal value, sign
/// or CSV schema is changed.
/// </summary>
internal sealed class ReportDocument : IDisposable
{
    private const int PageWidth = 827;
    private const int PageHeight = 1169;
    private const int Margin = 36;
    private const int Bottom = PageHeight - 46;

    private readonly FiscalDayReport _report;
    private readonly string? _pdfPath;
    private readonly Font _title = UiTheme.Ui(16f, FontStyle.Bold);
    private readonly Font _subtitle = UiTheme.Ui(9f);
    private readonly Font _label = UiTheme.Ui(8f, FontStyle.Bold);
    private readonly Font _body = UiTheme.Ui(8f);
    private readonly Font _small = UiTheme.Ui(7.5f);
    private readonly Font _number = UiTheme.Ui(14f, FontStyle.Bold);

    private int _rowIndex;
    private bool _summaryDrawn;

    private ReportDocument(FiscalDayReport report, string? pdfPath)
    {
        _report = report;
        _pdfPath = pdfPath;
    }

    public static void Print(FiscalDayReport report, string? pdfPath = null)
    {
        using var printer = new ReportDocument(report, pdfPath);
        using var document = new PrintDocument
        {
            DocumentName = $"Fiscal day report {report.BusinessDate}",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };

        if (pdfPath is not null)
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>()
                    .Any(name => string.Equals(name, "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Microsoft Print to PDF is not installed; enable the Windows optional feature to save a PDF report.");
            }

            document.PrinterSettings.PrinterName = "Microsoft Print to PDF";
            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = Path.GetFullPath(pdfPath);
            var directory = Path.GetDirectoryName(document.PrinterSettings.PrintFileName);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        document.DefaultPageSettings.PaperSize = new PaperSize("A4", PageWidth, PageHeight);
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.PrintPage += (_, e) => printer.DrawPage(e);
        document.Print();
    }

    private void DrawPage(PrintPageEventArgs e)
    {
        if (e.Graphics is not { } g)
        {
            return;
        }

        g.PageUnit = GraphicsUnit.Display;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        DrawHeader(g);
        var y = Margin + 74;
        if (!_summaryDrawn)
        {
            DrawSummary(g, y);
            y += 96;
            _summaryDrawn = true;
        }

        y = DrawTableHeader(g, y);
        while (_rowIndex < _report.Lines.Count && y + 26 < Bottom - 58)
        {
            DrawRow(g, _report.Lines[_rowIndex], y, _rowIndex % 2 == 1);
            _rowIndex++;
            y += 26;
        }

        if (_rowIndex < _report.Lines.Count)
        {
            e.HasMorePages = true;
            return;
        }

        DrawTotals(g, Bottom - 50);
        e.HasMorePages = false;
    }

    private void DrawHeader(Graphics g)
    {
        g.DrawRectangle(new Pen(UiTheme.Accent, 3), Margin, Margin, 42, 42);
        using var mark = new SolidBrush(UiTheme.Accent);
        g.DrawString("F", UiTheme.Ui(15f, FontStyle.Bold), mark, Margin + 12, Margin + 8);
        g.DrawString(_report.CompanyName ?? "FiscalStack", _title, BrushesBlack(), Margin + 56, Margin - 2);
        g.DrawString($"Daily Z-shift summary · {_report.BusinessDate}", _subtitle, Brush(UiTheme.Muted),
            Margin + 56, Margin + 28);
        g.DrawString($"Fiscal day {OrDash(_report.FiscalDayNo?.ToString())}   ·   Device {OrDash(_report.DeviceId)}",
            _small, Brush(UiTheme.Muted), PageWidth - Margin - 260, Margin + 28);
        Line(g, Margin, Margin + 58, PageWidth - Margin, UiTheme.Border);
    }

    private void DrawSummary(Graphics g, int y)
    {
        var width = (PageWidth - Margin * 2 - 24) / 4;
        DrawKpi(g, Margin, y, width, "Documents", _report.Documents.ToString(CultureInfo.InvariantCulture));
        DrawKpi(g, Margin + width + 8, y, width, "Gross", Money(_report.Gross));
        DrawKpi(g, Margin + (width + 8) * 2, y, width, "VAT", Money(_report.Tax));
        var attention = _report.HeldForReview + _report.Rejected + _report.UnknownOutcome + _report.WithoutItemDetail.Count;
        DrawKpi(g, Margin + (width + 8) * 3, y, width, "Needs attention", attention.ToString(CultureInfo.InvariantCulture));
    }

    private void DrawKpi(Graphics g, int x, int y, int width, string label, string value)
    {
        using var path = UiTheme.RoundedRectangle(new Rectangle(x, y, width, 74), 8);
        using var fill = new SolidBrush(UiTheme.Canvas);
        g.FillPath(fill, path);
        g.DrawPath(new Pen(UiTheme.Border), path);
        g.DrawString(label, _label, Brush(UiTheme.Muted), x + 12, y + 10);
        g.DrawString(Fit(g, value, _number, width - 24), _number, Brush(UiTheme.Text), x + 12, y + 30);
    }

    private int DrawTableHeader(Graphics g, int y)
    {
        using var fill = new SolidBrush(UiTheme.Navy);
        g.FillRectangle(fill, Margin, y, PageWidth - Margin * 2, 26);
        var columns = Columns();
        foreach (var column in columns)
        {
            var x = Margin + column.Left + 8;
            g.DrawString(column.Title, _label, Brushes.White, x, y + 7);
        }

        return y + 26;
    }

    private void DrawRow(Graphics g, FiscalDayLine line, int y, bool alternate)
    {
        if (alternate)
        {
            using var fill = new SolidBrush(UiTheme.Canvas);
            g.FillRectangle(fill, Margin, y, PageWidth - Margin * 2, 26);
        }

        var values = new[]
        {
            line.InvoiceNumber,
            ShortType(line.TransactionType),
            line.Line,
            line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
            Money(line.LineTotal, line.Currency),
            line.TaxType,
            line.TaxRatePercent is { } rate ? $"{rate:0.###}%" : "-",
            OrDash(line.FiscalCode)
        };
        var columns = Columns();
        for (var i = 0; i < columns.Count && i < values.Length; i++)
        {
            var column = columns[i];
            var font = i == 4 ? _body : _small;
            var text = Fit(g, values[i] ?? "-", font, column.Width - 12);
            var x = Margin + column.Left + 8;
            g.DrawString(text, font, Brush(i == 4 ? UiTheme.Text : UiTheme.Neutral), x, y + 7);
        }

        Line(g, Margin, y + 26, PageWidth - Margin, UiTheme.Border);
    }

    private void DrawTotals(Graphics g, int y)
    {
        Line(g, Margin, y, PageWidth - Margin, UiTheme.Navy, 2);
        g.DrawString("Day position", UiTheme.Ui(10f, FontStyle.Bold), Brush(UiTheme.Text), Margin, y + 10);
        g.DrawString($"Fiscalised documents: {_report.Documents}", _body, Brush(UiTheme.Neutral), Margin, y + 32);
        g.DrawString($"Gross: {Money(_report.Gross)}", _body, Brush(UiTheme.Neutral), Margin + 260, y + 32);
        g.DrawString($"VAT: {Money(_report.Tax)}", _body, Brush(UiTheme.Neutral), Margin + 480, y + 32);
        if (_report.GrossWithoutItemDetail != 0)
        {
            g.DrawString($"No fiscal copy left on till: {Money(_report.GrossWithoutItemDetail)}",
                _small, Brush(UiTheme.Warning), Margin, y + 52);
        }

        g.DrawString("Generated by PrintInterceptor from the till's fiscal ledger. FiscalStack remains the source of truth.",
            _small, Brush(UiTheme.Muted), Margin, PageHeight - 26);
    }

    private static IReadOnlyList<(string Title, int Left, int Width)> Columns() =>
    [
        ("Invoice", 0, 120),
        ("Type", 120, 82),
        ("Item", 202, 178),
        ("Qty", 380, 50),
        ("Total", 430, 92),
        ("Tax type", 522, 88),
        ("Rate", 610, 58),
        ("Fiscal code", 668, 123)
    ];

    private static string ShortType(string type) => type switch
    {
        "CreditNote" => "Credit note",
        "DebitNote" => "Debit note",
        _ => "Sale"
    };

    private static string Money(decimal value, string? currency = null)
    {
        var text = value.ToString("0.00", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(currency) ? text : $"{currency} {text}";
    }

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private static string Fit(Graphics g, string text, Font font, int width)
    {
        if (g.MeasureString(text, font).Width <= width)
        {
            return text;
        }

        var value = text;
        while (value.Length > 1 && g.MeasureString(value + "…", font).Width > width)
        {
            value = value[..^1];
        }

        return value + "…";
    }

    private static void Line(Graphics g, int x1, int y, int x2, Color color, float width = 1f) =>
        g.DrawLine(new Pen(color, width), x1, y, x2, y);

    private static Brush Brush(Color color) => new SolidBrush(color);

    private static Brush BrushesBlack() => Brushes.Black;

    public void Dispose()
    {
        _title.Dispose();
        _subtitle.Dispose();
        _label.Dispose();
        _body.Dispose();
        _small.Dispose();
        _number.Dispose();
    }
}
