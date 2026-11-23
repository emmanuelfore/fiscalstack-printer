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
        while (_rowIndex < _report.Lines.Count)
        {
            var line = _report.Lines[_rowIndex];
            var rowHeight = RowHeight(g, line);
            if (y + rowHeight > Bottom - 58)
            {
                e.HasMorePages = true;
                return;
            }

            DrawRow(g, line, y, rowHeight, _rowIndex % 2 == 1);
            _rowIndex++;
            y += rowHeight;
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
        using var mark = UiTheme.TryBrandMarkImage(42);
        if (mark is not null)
        {
            g.DrawImage(mark, Margin, Margin, 42, 42);
        }
        else
        {
            using var border = new Pen(UiTheme.Accent, 3);
            g.DrawRectangle(border, Margin, Margin, 42, 42);
            using var letter = new SolidBrush(UiTheme.Accent);
            using var letterFont = UiTheme.Ui(15f, FontStyle.Bold);
            g.DrawString("F", letterFont, letter, Margin + 12, Margin + 8);
        }

        g.DrawString(
            Fit(g, _report.CompanyName ?? "FiscalStack", _title, 430),
            _title, BrushesBlack(), Margin + 56, Margin - 2);
        g.DrawString($"Daily Z-shift summary · {_report.BusinessDate}", _subtitle, Brush(UiTheme.Muted),
            Margin + 56, Margin + 28);
        g.DrawString(
            Fit(g,
                $"Fiscal day {OrDash(_report.FiscalDayNo?.ToString())}   ·   Device {OrDash(_report.DeviceId)}",
                _small, 290),
            _small, Brush(UiTheme.Muted), PageWidth - Margin - 290, Margin + 28);
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

    /// <summary>Prints one fiscal line with the business text wrapped, never silently cut off.</summary>
    private void DrawRow(Graphics g, FiscalDayLine line, int y, int height, bool alternate)
    {
        if (alternate)
        {
            using var fill = new SolidBrush(UiTheme.Canvas);
            g.FillRectangle(fill, Margin, y, PageWidth - Margin * 2, height);
        }

        var columns = Columns();
        var cells = CellLines(g, line, columns);
        for (var i = 0; i < columns.Count && i < cells.Count; i++)
        {
            var column = columns[i];
            var font = i == 5 ? _body : _small;
            var brush = Brush(i == 5 ? UiTheme.Text : UiTheme.Neutral);
            var x = Margin + column.Left + 6;
            for (var row = 0; row < cells[i].Count; row++)
            {
                g.DrawString(cells[i][row], font, brush, x, y + 6 + row * 12);
            }
        }

        Line(g, Margin, y + height, PageWidth - Margin, UiTheme.Border);
    }

    private List<List<string>> CellLines(
        Graphics g,
        FiscalDayLine line,
        IReadOnlyList<(string Title, int Left, int Width)> columns)
    {
        var tax = new List<string> { OrDash(line.TaxType) };
        if (line.TaxRatePercent is { } rate)
        {
            tax.Add($"{rate:0.###}%");
        }

        var total = new List<string> { Money(line.LineTotal, line.Currency) };
        if (line.UnitPrice is { } unitPrice)
        {
            total.Add($"@ {unitPrice:0.###}");
        }

        var proof = new List<string> { OrDash(line.FiscalCode) };
        if (line.ReceiptNumber is { } receipt)
        {
            proof.Add($"Receipt {receipt}");
        }

        return
        [
            Wrap(g, line.BusinessDate, _small, columns[0].Width - 12, 1),
            Wrap(g, OrDash(line.InvoiceNumber), _small, columns[1].Width - 12, 2),
            Wrap(g, ShortType(line.TransactionType), _small, columns[2].Width - 12, 2),
            Wrap(g, OrDash(line.Line), _small, columns[3].Width - 12, 4),
            Wrap(g, line.Quantity.ToString("0.###", CultureInfo.InvariantCulture), _small, columns[4].Width - 12, 1),
            total,
            tax,
            proof
        ];
    }

    private int RowHeight(Graphics g, FiscalDayLine line)
    {
        var columns = Columns();
        var cells = CellLines(g, line, columns);
        return Math.Max(34, cells.Max(cell => cell.Count) * 12 + 10);
    }

    /// <summary>
    /// Wraps the complete value into the column. An ellipsis is only ever used when the four-line
    /// cap is reached, and the printed footer points the reader to the CSV for the complete text.
    /// </summary>
    private static List<string> Wrap(Graphics g, string text, Font font, int width, int maxLines)
    {
        var remaining = text.Trim();
        var lines = new List<string>();
        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            var fitted = Fit(g, remaining, font, width);
            lines.Add(fitted);
            if (fitted.EndsWith('…') || fitted.Length >= remaining.Length)
            {
                return lines;
            }

            remaining = remaining[fitted.Length..].Trim();
        }

        return lines;
    }

    private void DrawTotals(Graphics g, int y)
    {
        Line(g, Margin, y, PageWidth - Margin, UiTheme.Navy, 2);
        using var heading = UiTheme.Ui(10f, FontStyle.Bold);
        g.DrawString("Day position", heading, Brush(UiTheme.Text), Margin, y + 10);
        g.DrawString($"Fiscalised documents: {_report.Documents}", _body, Brush(UiTheme.Neutral), Margin, y + 32);
        g.DrawString($"Gross: {Money(_report.Gross)}", _body, Brush(UiTheme.Neutral), Margin + 260, y + 32);
        g.DrawString($"VAT: {Money(_report.Tax)}", _body, Brush(UiTheme.Neutral), Margin + 480, y + 32);
        if (_report.GrossWithoutItemDetail != 0)
        {
            g.DrawString($"No fiscal copy left on till: {Money(_report.GrossWithoutItemDetail)}",
                _small, Brush(UiTheme.Warning), Margin, y + 52);
        }

        g.DrawString("Item text wraps; Export CSV holds the complete line, buyer and receipt fields.",
            _small, Brush(UiTheme.Muted), Margin, PageHeight - 48);
        g.DrawString("Generated by PrintInterceptor from the till's fiscal ledger. FiscalStack remains the source of truth.",
            _small, Brush(UiTheme.Muted), Margin, PageHeight - 26);
    }

    private static IReadOnlyList<(string Title, int Left, int Width)> Columns() =>
    [
        ("Date", 0, 64),
        ("Invoice", 64, 102),
        ("Type", 166, 68),
        ("Item", 234, 202),
        ("Qty", 436, 42),
        ("Total", 478, 78),
        ("Tax type", 556, 72),
        ("Fiscal proof", 628, 127)
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
