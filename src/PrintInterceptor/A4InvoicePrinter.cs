using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Globalization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor;

/// <summary>
/// Renders the FiscalBridge "Modern Blue" A4 invoice with GDI. The layout is
/// mirrored from the Electron HTML template but draws directly through the
/// printer driver, so a session-0 service can print it without a browser.
/// </summary>
internal static class A4InvoicePrinter
{
    private const int A4Width = 827;   // 210 mm at 1/100 inch
    private const int A4Height = 1169; // 297 mm at 1/100 inch

    public static void Print(ReceiptPrintModel model, ILog log)
    {
        var document = new PrintDocument
        {
            DocumentName = $"Fiscal invoice {model.InvoiceNumber}",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };

        var printerName = model.EffectivePrinterName;
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>()
                    .Any(name => string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Printer '{printerName}' is not installed. Check printing.a4PrinterName.");
            }

            document.PrinterSettings.PrinterName = printerName;
        }

        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException("No valid A4 printer is available.");
        }

        document.PrinterSettings.Copies = (short)Math.Clamp(model.Copies, 1, 5);
        document.DefaultPageSettings.PaperSize = ResolveA4Paper(document) ??
                                                 new PaperSize("A4", A4Width, A4Height);
        document.DefaultPageSettings.Landscape = false;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        using var session = new InvoiceSession(model, log);
        document.PrintPage += (_, args) => session.DrawPage(args);
        document.Print();
    }

    /// <summary>
    /// Uses the driver's own A4 media entry when it has one; custom paper sizes
    /// are silently substituted on some drivers.
    /// </summary>
    private static PaperSize? ResolveA4Paper(PrintDocument document)
    {
        try
        {
            foreach (PaperSize size in document.PrinterSettings.PaperSizes)
            {
                if (size.Kind == PaperKind.A4)
                {
                    return size;
                }
            }
        }
        catch (InvalidPrinterException)
        {
            // No driver media list; fall back to the explicit A4 dimensions.
        }

        return null;
    }

    private sealed class InvoiceSession : IDisposable
    {
        private const float Margin = 34f;          // 24 pt
        private const float Gap = 14f;             // 10 pt
        private const float CardPadding = 13f;     // 9 pt
        private const float Radius = 8.5f;         // 6 pt
        private const float TableHeaderHeight = 30f;
        private const float FooterReserve = 300f;

        private readonly ReceiptPrintModel _model;
        private readonly ILog _log;

        private readonly Color _accent;
        private readonly Color _surface = ColorTranslator.FromHtml("#f8fafc");
        private readonly Color _text = ColorTranslator.FromHtml("#0f172a");
        private readonly Color _muted = ColorTranslator.FromHtml("#64748b");
        private readonly Color _body = ColorTranslator.FromHtml("#475569");
        private readonly Color _border = ColorTranslator.FromHtml("#dbe3ef");
        private readonly Color _zebra = ColorTranslator.FromHtml("#f8fafc");

        private readonly Font _title = new("Helvetica", 16f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _subtitle = new("Helvetica", 8f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _section = new("Helvetica", 9f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _bodyFont = new("Helvetica", 9f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _bodyBold = new("Helvetica", 9f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _small = new("Helvetica", 8f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _smallBold = new("Helvetica", 8f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _tiny = new("Helvetica", 7f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _company = new("Helvetica", 10f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _total = new("Helvetica", 12f, FontStyle.Bold, GraphicsUnit.Point);

        private readonly SolidBrush _textBrush;
        private readonly SolidBrush _mutedBrush;
        private readonly SolidBrush _bodyBrush;
        private readonly SolidBrush _accentBrush;
        private readonly SolidBrush _surfaceBrush;
        private readonly SolidBrush _zebraBrush;
        private readonly Pen _borderPen;
        private readonly Pen _accentPen;

        private Image? _qrImage;
        private Image? _logo;
        private bool _headerPrinted;
        private int _nextItem;

        public InvoiceSession(ReceiptPrintModel model, ILog log)
        {
            _model = model;
            _log = log;

            try
            {
                _accent = ColorTranslator.FromHtml(model.AccentColor);
            }
            catch
            {
                _accent = ColorTranslator.FromHtml("#2563eb");
            }

            _textBrush = new SolidBrush(_text);
            _mutedBrush = new SolidBrush(_muted);
            _bodyBrush = new SolidBrush(_body);
            _accentBrush = new SolidBrush(_accent);
            _surfaceBrush = new SolidBrush(_surface);
            _zebraBrush = new SolidBrush(_zebra);
            _borderPen = new Pen(_border, 0.8f);
            _accentPen = new Pen(_accent, 1.5f);

            LoadLogo();
        }

        private float Usable => A4Width - Margin * 2;

        public void DrawPage(PrintPageEventArgs e)
        {
            var g = e.Graphics
                ?? throw new InvalidOperationException("Print page has no graphics context.");
            g.PageUnit = GraphicsUnit.Display;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var pageBottom = e.PageBounds.Height - Margin;
            var y = Margin;

            if (!_headerPrinted)
            {
                y = DrawHeader(g, y);
                y = DrawTitle(g, y);
                y = DrawParties(g, y);
                y = DrawFiscalGrid(g, y);
                y = DrawRelatedBand(g, y);
                _headerPrinted = true;
            }
            else
            {
                y = DrawContinuedHeader(g, y);
            }

            y = DrawItems(g, y, pageBottom, out var itemsDone);
            if (!itemsDone)
            {
                e.HasMorePages = true;
                return;
            }

            if (y > pageBottom - FooterReserve)
            {
                e.HasMorePages = true;
                return;
            }

            y = DrawTaxAndTotals(g, y);
            y = DrawNotes(g, y);
            DrawFooter(g, y);
        }

        // ── Header ──────────────────────────────────────────────────────────

        private float DrawHeader(Graphics g, float y)
        {
            var top = y;
            const float logoWidth = 150f;   // 108 pt
            const float logoHeight = 69f;   // 50 pt
            const float qrSize = 94f;       // 68 pt
            var centerWidth = Usable * 0.44f;
            var centerX = Margin + (Usable - centerWidth) / 2f;

            if (_logo is not null)
            {
                DrawImageContained(g, _logo, new RectangleF(Margin, top, logoWidth, logoHeight));
            }

            var hasVerification = !string.IsNullOrWhiteSpace(_model.VerificationCode);
            var textY = top;
            if (hasVerification)
            {
                textY = DrawText(g, $"Verification Code: {_model.VerificationCode}",
                    _bodyBold, _textBrush, centerX, textY, centerWidth, StringAlignment.Center);
                var verifyUrl = string.IsNullOrWhiteSpace(_model.VerifyUrl)
                    ? "https://fdms.zimra.co.zw"
                    : _model.VerifyUrl!;
                DrawText(g, $"Verify at {verifyUrl}", _tiny, Brushes.Blue,
                    centerX, textY, centerWidth, StringAlignment.Center);
            }

            var contentBottom = Math.Max(top + logoHeight, top + qrSize);
            var qrTop = contentBottom - qrSize;
            if (!string.IsNullOrWhiteSpace(_model.QrCodeUrl))
            {
                try
                {
                    _qrImage ??= CreateImage(_model.QrCodeUrl!);
                    g.DrawImage(_qrImage, Margin + Usable - qrSize, qrTop, qrSize, qrSize);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not render the A4 fiscal QR code: {ex.Message}");
                }
            }

            y = contentBottom + Gap;
            g.DrawLine(_borderPen, Margin, y, Margin + Usable, y);
            return y + Gap;
        }

        private float DrawContinuedHeader(Graphics g, float y)
        {
            var title = string.IsNullOrWhiteSpace(_model.CompanyName) ? "FISCAL INVOICE" : _model.CompanyName;
            y = DrawText(g, title, _company, _accentBrush, Margin, y, Usable, StringAlignment.Center);
            y = DrawText(g, $"Invoice {_model.InvoiceNumber} (continued)", _small, _mutedBrush,
                Margin, y, Usable, StringAlignment.Center);
            y += 6;
            g.DrawLine(_borderPen, Margin, y, Margin + Usable, y);
            return y + Gap;
        }

        private float DrawTitle(Graphics g, float y)
        {
            y = DrawText(g, DocumentTitle(), _title, _textBrush, Margin, y, Usable, StringAlignment.Center);
            y += 2;

            const float barWidth = 83f;   // 60 pt
            using (var path = RoundedRectangle(Margin + (Usable - barWidth) / 2f, y + 2, barWidth, 3f, 1.5f))
            {
                g.FillPath(_accentBrush, path);
            }

            y += 12;
            var subtitle = _model.IsOffline
                ? "ZIMRA fiscal document · signed offline, queued for sync"
                : "ZIMRA fiscal document";

            if (_model.IsReprint)
            {
                subtitle += " · REPRINT COPY";
            }

            y = DrawText(g, subtitle, _subtitle, _mutedBrush, Margin, y, Usable, StringAlignment.Center);
            return y + Gap + 2;
        }

        private string DocumentTitle()
        {
            if (string.Equals(_model.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase))
            {
                return "CREDIT NOTE";
            }

            if (string.Equals(_model.TransactionType, "DebitNote", StringComparison.OrdinalIgnoreCase))
            {
                return "DEBIT NOTE";
            }

            return string.IsNullOrWhiteSpace(_model.CompanyVatNumber) ? "FISCAL INVOICE" : "FISCAL TAX INVOICE";
        }

        // ── Seller / buyer cards ────────────────────────────────────────────

        private float DrawParties(Graphics g, float y)
        {
            var cardWidth = (Usable - Gap) / 2f;
            var sellerX = Margin;
            var buyerX = Margin + cardWidth + Gap;

            var sellerLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(_model.CompanyAddress))
            {
                sellerLines.Add(_model.CompanyAddress);
            }

            sellerLines.Add($"TIN: {OrNa(_model.CompanyTin)}");
            sellerLines.Add($"VAT No: {OrNa(_model.CompanyVatNumber)}");
            if (!string.IsNullOrWhiteSpace(_model.CompanyPhone))
            {
                sellerLines.Add($"Phone: {_model.CompanyPhone}");
            }

            if (!string.IsNullOrWhiteSpace(_model.CompanyEmail))
            {
                sellerLines.Add($"Email: {_model.CompanyEmail}");
            }

            var buyerLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(_model.BuyerAddress))
            {
                buyerLines.Add(_model.BuyerAddress!);
            }

            buyerLines.Add($"TIN: {OrNa(_model.BuyerTin)}");
            buyerLines.Add($"VAT No: {OrNa(_model.BuyerVatNumber)}");
            if (!string.IsNullOrWhiteSpace(_model.BuyerEmail))
            {
                buyerLines.Add($"Email: {_model.BuyerEmail}");
            }

            if (!string.IsNullOrWhiteSpace(_model.BuyerPhone))
            {
                buyerLines.Add($"Phone: {_model.BuyerPhone}");
            }

            var walkIn = string.IsNullOrWhiteSpace(_model.BuyerName);
            var textWidth = cardWidth - CardPadding * 2;

            var sellerHeight = CardHeight(g, sellerLines, textWidth, _model.CompanyName);
            var buyerHeight = walkIn
                ? CardHeight(g, new List<string>(), textWidth, null)
                : CardHeight(g, buyerLines, textWidth, _model.BuyerName);

            var cardHeight = Math.Max(sellerHeight, buyerHeight);

            DrawCard(g, sellerX, y, cardWidth, cardHeight);
            var cursor = y + CardPadding;
            cursor = DrawSectionTitle(g, "SELLER", sellerX + CardPadding, cursor, textWidth);
            cursor = DrawText(g, string.IsNullOrWhiteSpace(_model.CompanyName) ? "-" : _model.CompanyName,
                _company, _textBrush, sellerX + CardPadding, cursor, textWidth);
            foreach (var line in sellerLines)
            {
                cursor = DrawText(g, line, _bodyFont, _bodyBrush,
                    sellerX + CardPadding, cursor, textWidth);
            }

            DrawCard(g, buyerX, y, cardWidth, cardHeight);
            cursor = y + CardPadding;
            cursor = DrawSectionTitle(g, "BUYER", buyerX + CardPadding, cursor, textWidth);
            if (walkIn)
            {
                var walkInFont = new Font(_bodyFont, FontStyle.Italic);
                using (walkInFont)
                {
                    DrawText(g, "Walk-in Customer", walkInFont, _mutedBrush,
                        buyerX + CardPadding, cursor + 2, textWidth);
                }
            }
            else
            {
                cursor = DrawText(g, _model.BuyerName!, _company, _textBrush,
                    buyerX + CardPadding, cursor, textWidth);
                foreach (var line in buyerLines)
                {
                    cursor = DrawText(g, line, _bodyFont, _bodyBrush,
                        buyerX + CardPadding, cursor, textWidth);
                }
            }

            return y + cardHeight + Gap + 2;
        }

        private float CardHeight(Graphics g, IReadOnlyList<string> lines, float textWidth, string? name)
        {
            var height = CardPadding * 2 + LineHeight(g, _section) + 6;
            if (!string.IsNullOrWhiteSpace(name))
            {
                height += LineHeight(g, _company) + 2;
            }

            foreach (var line in lines)
            {
                height += g.MeasureString(line, _bodyFont, new SizeF(textWidth, 10000f)).Height;
            }

            return height;
        }

        private float DrawSectionTitle(Graphics g, string title, float x, float y, float width)
        {
            y = DrawText(g, title, _section, _accentBrush, x, y, width);
            g.DrawLine(_borderPen, x, y + 1, x + width, y + 1);
            return y + 7;
        }

        // ── Fiscal grid ─────────────────────────────────────────────────────

        private float DrawFiscalGrid(Graphics g, float y)
        {
            var height = CardPadding * 2 + 4 * 17f;
            DrawCard(g, Margin, y, Usable, height);
            g.FillRectangle(_accentBrush, Margin, y + 4, 4f, height - 8); // 3 pt accent spine

            var leftX = Margin + CardPadding + 6;
            var columnWidth = Usable * 0.46f;
            var rightX = Margin + Usable * 0.52f;

            var hasCounters = _model.ReceiptCounter is not null && _model.ReceiptGlobalNo is not null;
            var invoiceValue = hasCounters
                ? $"{_model.ReceiptCounter}/{_model.ReceiptGlobalNo}"
                : OrNa(_model.InvoiceNumber);

            var left = new List<(string Label, string Value)>
            {
                ("Invoice No:", invoiceValue),
                ("Customer Reference No:", OrNa(_model.InvoiceNumber)),
                ("Fiscal Day No:", _model.FiscalDayNo?.ToString(CultureInfo.InvariantCulture) ?? "N/A")
            };

            var right = new List<(string Label, string Value)>
            {
                ("Date:", OrNa(_model.DateText)),
                ("Fiscal Device ID:", OrNa(_model.DeviceId)),
                ("Device Serial No:", OrNa(_model.DeviceSerialNo))
            };

            var rowY = y + CardPadding;
            foreach (var (label, value) in left)
            {
                DrawLabelValue(g, label, value, leftX, rowY, columnWidth);
                rowY += 17f;
            }

            rowY = y + CardPadding;
            foreach (var (label, value) in right)
            {
                DrawLabelValue(g, label, value, rightX, rowY, columnWidth);
                rowY += 17f;
            }

            return y + height + Gap;
        }

        private void DrawLabelValue(Graphics g, string label, string value, float x, float y, float width)
        {
            var labelWidth = width * 0.48f;
            DrawText(g, label, _bodyFont, _mutedBrush, x, y, labelWidth, StringAlignment.Near, wrap: false);
            DrawText(g, value, _bodyFont, _textBrush, x + labelWidth, y, width - labelWidth,
                StringAlignment.Near, wrap: false);
        }

        private float DrawRelatedBand(Graphics g, float y)
        {
            var isNote = string.Equals(_model.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(_model.TransactionType, "DebitNote", StringComparison.OrdinalIgnoreCase);
            if (!isNote || string.IsNullOrWhiteSpace(_model.RelatedInvoiceNumber))
            {
                return y;
            }

            var kind = string.Equals(_model.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase)
                ? "CREDITED INVOICE"
                : "DEBITED INVOICE";

            var height = 44f;
            using (var fill = new SolidBrush(_zebra))
            {
                g.FillRectangle(fill, Margin, y, Usable, height);
            }

            g.DrawLine(_borderPen, Margin, y, Margin + Usable, y);
            g.DrawLine(_borderPen, Margin, y + height, Margin + Usable, y + height);

            DrawText(g, $"{kind}  |  Original Fiscal Reference", _smallBold, _bodyBrush,
                Margin + 6, y + 6, Usable - 12, StringAlignment.Near, wrap: false);

            var reference = $"Inv No: {_model.RelatedInvoiceNumber}   " +
                            $"Date: {OrNa(_model.RelatedInvoiceDate)}   " +
                            $"ID: {OrNa(_model.DeviceId)}";
            DrawText(g, reference, _tiny, _mutedBrush, Margin + 6, y + 22, Usable - 12,
                StringAlignment.Near, wrap: false);

            return y + height + Gap;
        }

        // ── Items table ─────────────────────────────────────────────────────

        private float DrawItems(Graphics g, float y, float pageBottom, out bool done)
        {
            if (_nextItem >= _model.Items.Count && _nextItem > 0)
            {
                done = true;
                return y;
            }

            var inclusive = _model.TaxInclusive;
            var columns = inclusive
                ? new[]
                {
                    new Column("Code", 0.10f, StringAlignment.Near),
                    new Column("Description", 0.40f, StringAlignment.Near),
                    new Column("Qty", 0.08f, StringAlignment.Center),
                    new Column("Price", 0.12f, StringAlignment.Far),
                    new Column("VAT", 0.15f, StringAlignment.Far),
                    new Column("Total (incl.)", 0.15f, StringAlignment.Far)
                }
                : new[]
                {
                    new Column("Code", 0.08f, StringAlignment.Near),
                    new Column("Description", 0.33f, StringAlignment.Near),
                    new Column("Qty", 0.08f, StringAlignment.Center),
                    new Column("Price", 0.10f, StringAlignment.Far),
                    new Column("Amount (excl.)", 0.13f, StringAlignment.Far),
                    new Column("VAT", 0.12f, StringAlignment.Far),
                    new Column("Total (incl.)", 0.16f, StringAlignment.Far)
                };

            var widths = new float[columns.Length];
            var used = 0f;
            for (var index = 0; index < columns.Length; index++)
            {
                widths[index] = index == columns.Length - 1
                    ? Usable - used
                    : (float)Math.Floor(Usable * columns[index].Width);
                used += widths[index];
            }

            DrawTableHeader(g, y, columns, widths);
            y += TableHeaderHeight;
            var firstRowY = y;

            var descriptionColumn = Array.FindIndex(columns, c => c.Title == "Description");
            var descriptionWidth = widths[descriptionColumn] - 12f;

            while (_nextItem < _model.Items.Count)
            {
                var item = _model.Items[_nextItem];
                var descriptionHeight = g.MeasureString(
                    item.Name,
                    _bodyFont,
                    new SizeF(descriptionWidth, 10000f)).Height;
                var rowHeight = descriptionHeight + 14f;

                if (y > firstRowY && y + rowHeight > pageBottom - FooterReserve)
                {
                    done = false;
                    return y;
                }

                if (_nextItem % 2 == 1)
                {
                    g.FillRectangle(_zebraBrush, Margin, y, Usable, rowHeight);
                }

                DrawItemRow(g, item, y, rowHeight, columns, widths);
                g.DrawLine(_borderPen, Margin, y + rowHeight, Margin + Usable, y + rowHeight);

                y += rowHeight;
                _nextItem++;
            }

            done = true;
            return y + 6;
        }

        private void DrawTableHeader(Graphics g, float y, Column[] columns, float[] widths)
        {
            using (var path = RoundedRectangle(Margin, y, Usable, TableHeaderHeight, Radius))
            {
                g.FillPath(_accentBrush, path);
            }

            var x = Margin;
            for (var index = 0; index < columns.Length; index++)
            {
                DrawText(g, columns[index].Title, _smallBold, Brushes.White,
                    x + 6, y + 9, widths[index] - 12, columns[index].Alignment, wrap: false);
                x += widths[index];
            }
        }

        private void DrawItemRow(
            Graphics g,
            ReceiptPrintLine item,
            float y,
            float height,
            Column[] columns,
            float[] widths)
        {
            var rate = item.TaxRate ?? 0m;
            var exempt = item.TaxType?.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase) == true;
            var unitPrice = item.UnitPrice ?? (item.Quantity != 0 ? item.Amount / item.Quantity : item.Amount);

            decimal vat;
            if (exempt || rate == 0m)
            {
                vat = 0m;
            }
            else if (_model.TaxInclusive)
            {
                vat = item.Amount - item.Amount / (1m + rate / 100m);
            }
            else
            {
                vat = item.Amount * rate / 100m;
            }

            var amountExcl = _model.TaxInclusive ? item.Amount - vat : item.Amount;
            var totalIncl = _model.TaxInclusive ? item.Amount : item.Amount + vat;

            var cells = _model.TaxInclusive
                ? new[]
                {
                    item.Code ?? "0000",
                    item.Name,
                    Qty(item.Quantity),
                    Money(unitPrice),
                    exempt ? "-" : (rate == 0m ? "0.00" : Money(vat)),
                    Money(totalIncl)
                }
                : new[]
                {
                    item.Code ?? "0000",
                    item.Name,
                    Qty(item.Quantity),
                    Money(unitPrice),
                    Money(amountExcl),
                    exempt ? "-" : (rate == 0m ? "0.00" : Money(vat)),
                    Money(totalIncl)
                };

            var x = Margin;
            for (var index = 0; index < cells.Length; index++)
            {
                var isLast = index == cells.Length - 1;
                var brush = index == 0
                    ? _mutedBrush
                    : isLast
                        ? _textBrush
                        : columns[index].Title == "VAT" ? _mutedBrush : _textBrush;
                var font = isLast ? _smallBold : columns[index].Title == "VAT" ? _small : _bodyFont;

                DrawText(g, cells[index], font, brush,
                    x + 6, y + 6, widths[index] - 12, columns[index].Alignment,
                    wrap: index == 1);

                x += widths[index];
            }
        }

        // ── Tax analysis and totals ─────────────────────────────────────────

        private float DrawTaxAndTotals(Graphics g, float y)
        {
            var cardWidth = (Usable - Gap) / 2f;
            var taxX = Margin;
            var totalsX = Margin + cardWidth + Gap;

            var taxRows = _model.Taxes;
            var taxHeight = CardPadding * 2 + LineHeight(g, _section) + 8 +
                            LineHeight(g, _small) + taxRows.Count * 15f;
            var totalsHeight = CardPadding * 2 + 4 * 15f + 34f;
            var cardHeight = Math.Max(taxHeight, totalsHeight);

            DrawCard(g, taxX, y, cardWidth, cardHeight);
            var cursor = y + CardPadding;
            cursor = DrawSectionTitle(g, "TAX ANALYSIS", taxX + CardPadding, cursor, cardWidth - CardPadding * 2);

            var innerWidth = cardWidth - CardPadding * 2;
            var columns = new[] { 0.25f, 0.25f, 0.25f, 0.25f };
            var headers = new[] { "VAT %", "Net.Amt", "VAT", "Amount" };
            var x = taxX + CardPadding;
            for (var index = 0; index < headers.Length; index++)
            {
                DrawText(g, headers[index], _tiny, _mutedBrush,
                    x, cursor, innerWidth * columns[index] - 2,
                    index == 0 ? StringAlignment.Near : StringAlignment.Far, wrap: false);
                x += innerWidth * columns[index];
            }

            cursor += LineHeight(g, _tiny) + 2;
            g.DrawLine(_borderPen, taxX + CardPadding, cursor, taxX + cardWidth - CardPadding, cursor);
            cursor += 2;

            if (taxRows.Count == 0)
            {
                DrawText(g, "-", _small, _mutedBrush, taxX + CardPadding, cursor, innerWidth,
                    StringAlignment.Near, wrap: false);
            }

            foreach (var row in taxRows)
            {
                var label = row.IsExempt
                    ? "Exempt"
                    : row.Rate is { } rate ? $"{rate.ToString("0.00", CultureInfo.InvariantCulture)}%" : "-";
                var cells = new[]
                {
                    label,
                    Money(row.Net),
                    row.IsExempt ? "-" : (row.Rate is null or 0m ? "0.00" : Money(row.Tax)),
                    Money(row.Gross)
                };

                x = taxX + CardPadding;
                for (var index = 0; index < cells.Length; index++)
                {
                    DrawText(g, cells[index], _small, index == 0 ? _bodyBrush : _textBrush,
                        x, cursor, innerWidth * columns[index] - 2,
                        index == 0 ? StringAlignment.Near : StringAlignment.Far, wrap: false);
                    x += innerWidth * columns[index];
                }

                cursor += 15f;
            }

            DrawTotalsCard(g, totalsX, y, cardWidth, cardHeight);
            return y + cardHeight + Gap;
        }

        private void DrawTotalsCard(Graphics g, float x, float y, float width, float height)
        {
            DrawCard(g, x, y, width, height);
            var innerWidth = width - CardPadding * 2;
            var cursor = y + CardPadding;

            var quantity = _model.Items.Sum(item => item.Quantity);
            var rows = new (string Label, string Value, bool Bold)[]
            {
                ("Number of Items", Qty(quantity), true),
                ("Total (excl. tax)", Money(_model.Subtotal), false),
                ("Total VAT", Money(_model.TaxTotal), false),
                ($"Invoice total, {_model.Currency}", Money(_model.Total), false)
            };

            foreach (var (label, value, bold) in rows)
            {
                var font = bold ? _smallBold : _small;
                DrawText(g, label, font, bold ? _textBrush : _bodyBrush,
                    x + CardPadding, cursor, innerWidth * 0.6f, StringAlignment.Near, wrap: false);
                DrawText(g, value, font, _textBrush,
                    x + CardPadding + innerWidth * 0.6f, cursor, innerWidth * 0.4f,
                    StringAlignment.Far, wrap: false);
                cursor += 15f;
            }

            cursor += 5;
            g.DrawLine(_accentPen, x + CardPadding, cursor, x + width - CardPadding, cursor);
            cursor += 8;

            var totalSize = g.MeasureString($"{_model.Currency} {Money(_model.Total)}", _total).Height;
            DrawText(g, "Total amount (incl. tax)", _smallBold, _textBrush,
                x + CardPadding, cursor + (totalSize - LineHeight(g, _smallBold)) / 2f,
                innerWidth * 0.48f, StringAlignment.Near, wrap: false);
            DrawText(g, $"{_model.Currency} {Money(_model.Total)}", _total, _textBrush,
                x + CardPadding + innerWidth * 0.48f, cursor, innerWidth * 0.52f, StringAlignment.Far);
        }

        // ── Notes and footer ───────────────────────────────────────────────

        private float DrawNotes(Graphics g, float y)
        {
            if (string.IsNullOrWhiteSpace(_model.Notes))
            {
                return y;
            }

            var width = Usable;
            var textWidth = width - CardPadding * 2;
            var height = CardPadding * 2 + LineHeight(g, _section) + 6 +
                         g.MeasureString(_model.Notes, _bodyFont, new SizeF(textWidth, 10000f)).Height;

            DrawCard(g, Margin, y, width, height);
            var cursor = y + CardPadding;
            cursor = DrawText(g, "NOTES", _smallBold, _accentBrush, Margin + CardPadding, cursor, textWidth);
            DrawText(g, _model.Notes!, _bodyFont, _bodyBrush, Margin + CardPadding, cursor, textWidth);
            return y + height + Gap;
        }

        private void DrawFooter(Graphics g, float y)
        {
            g.DrawLine(_borderPen, Margin, y, Margin + Usable, y);
            y += 12;

            var contact = string.Join("  |  ", new[]
            {
                _model.CompanyName,
                _model.CompanyPhone,
                _model.CompanyEmail
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (contact.Length > 0)
            {
                y = DrawText(g, contact, _small, _mutedBrush, Margin, y, Usable, StringAlignment.Center);
            }

            var footer = string.IsNullOrWhiteSpace(_model.FooterText)
                ? "Thank you for your business."
                : _model.FooterText;
            if (_model.IsOffline)
            {
                footer += "  ·  Offline fiscal receipt pending ZIMRA sync.";
            }

            DrawText(g, footer, _tiny, _mutedBrush, Margin, y + 2, Usable, StringAlignment.Center);
        }

        // ── Primitives ─────────────────────────────────────────────────────

        private void DrawCard(Graphics g, float x, float y, float width, float height)
        {
            using var path = RoundedRectangle(x, y, width, height, Radius);
            g.FillPath(_surfaceBrush, path);
            g.DrawPath(_borderPen, path);
        }

        private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
        {
            var diameter = Math.Min(radius * 2, Math.Min(width, height));
            var path = new GraphicsPath();
            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static float DrawText(
            Graphics g,
            string text,
            Font font,
            Brush brush,
            float x,
            float y,
            float width,
            StringAlignment alignment = StringAlignment.Near,
            bool wrap = true)
        {
            using var format = new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Near,
                Trimming = wrap ? StringTrimming.None : StringTrimming.EllipsisCharacter,
                FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap
            };

            var size = g.MeasureString(text, font, new SizeF(Math.Max(width, 1f), 10000f), format);
            g.DrawString(text, font, brush, new RectangleF(x, y, Math.Max(width, 1f), size.Height + 2), format);
            return y + size.Height;
        }

        private static float LineHeight(Graphics g, Font font) => font.GetHeight(g) + 2;

        private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string OrNa(string? value) => string.IsNullOrWhiteSpace(value) ? "N/A" : value!;

        private static void DrawImageContained(Graphics g, Image image, RectangleF bounds)
        {
            var scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            g.DrawImage(image, bounds.X, bounds.Y + (bounds.Height - height) / 2f, width, height);
        }

        private void LoadLogo()
        {
            if (string.IsNullOrWhiteSpace(_model.CompanyLogoPath) || !File.Exists(_model.CompanyLogoPath))
            {
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(_model.CompanyLogoPath);
                using var stream = new MemoryStream(bytes);
                using var loaded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                _logo = new Bitmap(loaded);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not load the company logo '{_model.CompanyLogoPath}': {ex.Message}");
            }
        }

        private static Image CreateImage(string qrText)
        {
            var png = QrCodeGenerator.CreatePng(qrText);
            using var stream = new MemoryStream(png);
            using var loaded = Image.FromStream(stream);
            return new Bitmap(loaded);
        }

        public void Dispose()
        {
            _title.Dispose();
            _subtitle.Dispose();
            _section.Dispose();
            _bodyFont.Dispose();
            _bodyBold.Dispose();
            _small.Dispose();
            _smallBold.Dispose();
            _tiny.Dispose();
            _company.Dispose();
            _total.Dispose();
            _textBrush.Dispose();
            _mutedBrush.Dispose();
            _bodyBrush.Dispose();
            _accentBrush.Dispose();
            _surfaceBrush.Dispose();
            _zebraBrush.Dispose();
            _borderPen.Dispose();
            _accentPen.Dispose();
            _qrImage?.Dispose();
            _logo?.Dispose();
        }

        private readonly record struct Column(string Title, float Width, StringAlignment Alignment);
    }
}
