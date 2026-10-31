using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Globalization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor;

/// <summary>
/// Prints the fiscal receipt through the Windows GDI printer driver at 1/100 inch
/// units, so the same layout works on 58 mm and 80 mm thermal printers.
/// </summary>
public sealed class WindowsGdiReceiptPrinter : IReceiptPrinter
{
    private readonly ILog _log;

    public WindowsGdiReceiptPrinter(ILog log)
    {
        _log = log;
    }

    public void Print(ReceiptPrintModel model)
    {
        if (model.Format == PrintFormat.A4)
        {
            A4InvoicePrinter.Print(model, _log);
            return;
        }

        var document = new PrintDocument
        {
            DocumentName = $"Fiscal receipt {model.InvoiceNumber}",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };

        if (!string.IsNullOrWhiteSpace(model.PrinterName))
        {
            if (!IsInstalled(model.PrinterName))
            {
                throw new InvalidOperationException(
                    $"Printer '{model.PrinterName}' is not installed. Check printing.printerName.");
            }

            document.PrinterSettings.PrinterName = model.PrinterName;
        }

        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException("No valid receipt printer is available.");
        }

        document.PrinterSettings.Copies = (short)Math.Clamp(model.Copies, 1, 5);

        var width = (int)Math.Round(model.PaperWidthMm / 25.4 * 100.0);
        document.DefaultPageSettings.PaperSize = new PaperSize("FiscalReceipt", width, 2000);
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        using var session = new PrintSession(model, width, _log);
        document.PrintPage += (_, args) => session.DrawPage(args);
        document.Print();
    }

    private static bool IsInstalled(string name) =>
        PrinterSettings.InstalledPrinters
            .Cast<string>()
            .Any(printer => string.Equals(printer, name, StringComparison.OrdinalIgnoreCase));

    private sealed class PrintSession : IDisposable
    {
        private const float QtyWidth = 34f;
        private const float AmountWidth = 58f;
        private const float HeaderReserve = 230f;

        private readonly ReceiptPrintModel _model;
        private readonly ILog _log;
        private readonly int _width;
        private readonly float _margin = 6f;
        private readonly float _usable;
        private readonly float _nameWidth;

        private readonly Font _fontCompany;
        private readonly Font _fontBold;
        private readonly Font _fontRegular;
        private readonly Font _fontSmall;
        private readonly Color _accent;
        private readonly Brush _accentBrush;
        private readonly Pen _accentPen;
        private readonly Pen _dashPen;

        private Image? _qrImage;
        private int _nextItem;
        private bool _headerPrinted;
        private bool _itemsDone;

        public PrintSession(ReceiptPrintModel model, int width, ILog log)
        {
            _model = model;
            _log = log;
            _width = width;
            _usable = width - _margin * 2;
            _nameWidth = _usable - QtyWidth - AmountWidth - 6;

            var narrow = model.PaperWidthMm <= 58;
            _fontCompany = new Font("Segoe UI", narrow ? 9f : 11f, FontStyle.Bold, GraphicsUnit.Point);
            _fontBold = new Font("Segoe UI", narrow ? 7.5f : 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            _fontRegular = new Font("Segoe UI", narrow ? 7f : 8f, FontStyle.Regular, GraphicsUnit.Point);
            _fontSmall = new Font("Segoe UI", narrow ? 6.5f : 7f, FontStyle.Regular, GraphicsUnit.Point);

            Color accent;
            try
            {
                accent = ColorTranslator.FromHtml(model.AccentColor);
            }
            catch
            {
                accent = Color.FromArgb(37, 99, 235);
            }

            _accent = accent;
            _accentBrush = new SolidBrush(accent);
            _accentPen = new Pen(accent, 1.5f);
            _dashPen = new Pen(Color.LightGray, 0.8f) { DashStyle = DashStyle.Dash };
        }

        public void DrawPage(PrintPageEventArgs e)
        {
            var graphics = e.Graphics
                ?? throw new InvalidOperationException("Print page has no graphics context.");
            graphics.PageUnit = GraphicsUnit.Display; // 1/100 inch
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float y = _margin;
            var pageBottom = e.PageBounds.Height - _margin;

            if (!_headerPrinted)
            {
                y = DrawHeader(graphics, y);
                _headerPrinted = true;
            }
            else
            {
                y = DrawText(graphics, "FISCAL RECEIPT (continued)", _fontBold, _accentBrush,
                    y, _usable, StringAlignment.Center);
                y += 6;
            }

            if (!_itemsDone)
            {
                y = DrawItemsHeader(graphics, y);

                while (_nextItem < _model.Items.Count)
                {
                    var item = _model.Items[_nextItem];
                    var rowHeight = Math.Max(
                        graphics.MeasureString(item.Name, _fontRegular, new SizeF(_nameWidth, 10000f)).Height,
                        LineHeight(graphics, _fontRegular)) + 4;

                    if (_nextItem > 0 && y + rowHeight > pageBottom - HeaderReserve)
                    {
                        e.HasMorePages = true;
                        return;
                    }

                    DrawItemRow(graphics, item, y);
                    y += rowHeight;
                    _nextItem++;
                }

                _itemsDone = true;
            }

            if (y > pageBottom - HeaderReserve)
            {
                e.HasMorePages = true;
                return;
            }

            y = DrawTotals(graphics, y);
            DrawFiscalBlock(graphics, y);
        }

        private float DrawHeader(Graphics g, float y)
        {
            var company = string.IsNullOrWhiteSpace(_model.CompanyName) ? "FISCAL RECEIPT" : _model.CompanyName.ToUpperInvariant();
            y = DrawText(g, company, _fontCompany, _accentBrush, y, _usable, StringAlignment.Center);
            y += 2;

            if (_model.IsReprint)
            {
                y = DrawText(g, "*** REPRINT — COPY ***", _fontBold, _accentBrush, y, _usable, StringAlignment.Center);
                y += 2;
            }

            var tinVat = string.Join("  |  ", new[]
            {
                string.IsNullOrWhiteSpace(_model.CompanyTin) ? null : $"TIN: {_model.CompanyTin}",
                string.IsNullOrWhiteSpace(_model.CompanyVatNumber) ? null : $"VAT: {_model.CompanyVatNumber}"
            }.Where(value => value is not null));

            if (tinVat.Length > 0)
            {
                y = DrawText(g, tinVat, _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
            }

            if (!string.IsNullOrWhiteSpace(_model.CompanyAddress))
            {
                y = DrawText(g, _model.CompanyAddress, _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
            }

            if (!string.IsNullOrWhiteSpace(_model.CompanyPhone))
            {
                y = DrawText(g, $"Tel: {_model.CompanyPhone}", _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
            }

            y += 4;
            g.DrawLine(_accentPen, _margin, y, _margin + _usable, y);
            y += 8;

            y = DrawKeyValue(g, "Invoice", _model.InvoiceNumber, y, bold: true);
            y = DrawKeyValue(g, "Date", _model.DateText, y);

            if (!string.IsNullOrWhiteSpace(_model.BuyerName))
            {
                y = DrawKeyValue(g, "Buyer", _model.BuyerName!, y);
            }

            y += 2;
            g.DrawLine(_dashPen, _margin, y, _margin + _usable, y);
            return y + 6;
        }

        private float DrawItemsHeader(Graphics g, float y)
        {
            DrawAligned(g, "ITEM", _fontBold, _accentBrush, _margin, _nameWidth, StringAlignment.Near, y);
            DrawAligned(g, "QTY", _fontBold, _accentBrush,
                _margin + _nameWidth + 6, QtyWidth, StringAlignment.Far, y);
            DrawAligned(g, "AMOUNT", _fontBold, _accentBrush,
                _margin + _usable - AmountWidth, AmountWidth, StringAlignment.Far, y);
            y += LineHeight(g, _fontBold) + 2;
            g.DrawLine(_dashPen, _margin, y, _margin + _usable, y);
            return y + 4;
        }

        private void DrawItemRow(Graphics g, ReceiptPrintLine item, float y)
        {
            var nameHeight = g.MeasureString(item.Name, _fontRegular, new SizeF(_nameWidth, 10000f)).Height;
            g.DrawString(item.Name, _fontRegular, Brushes.Black,
                new RectangleF(_margin, y, _nameWidth, nameHeight + 2));

            DrawAligned(g, item.Quantity.ToString("0.##", CultureInfo.InvariantCulture), _fontRegular,
                Brushes.Black, _margin + _nameWidth + 6, QtyWidth, StringAlignment.Far, y);
            DrawAligned(g, item.Amount.ToString("0.00", CultureInfo.InvariantCulture), _fontRegular,
                Brushes.Black, _margin + _usable - AmountWidth, AmountWidth, StringAlignment.Far, y);
        }

        private float DrawTotals(Graphics g, float y)
        {
            g.DrawLine(_dashPen, _margin, y, _margin + _usable, y);
            y += 6;

            y = DrawKeyValue(g, "VAT", _model.TaxTotal.ToString("0.00", CultureInfo.InvariantCulture), y);

            var boxHeight = 26f;
            using (var fill = new SolidBrush(Color.FromArgb(22, _accent.R, _accent.G, _accent.B)))
            {
                g.FillRectangle(fill, _margin, y, _usable, boxHeight);
            }

            g.DrawRectangle(_accentPen, _margin, y, _usable, boxHeight);
            DrawAligned(g, "TOTAL", _fontBold, _accentBrush, _margin + 4, _usable - 8, StringAlignment.Near, y + 7);
            DrawAligned(g, $"{_model.Currency} {_model.Total.ToString("0.00", CultureInfo.InvariantCulture)}",
                _fontBold, _accentBrush, _margin + 4, _usable - 8, StringAlignment.Far, y + 7);
            y += boxHeight + 6;

            y = DrawKeyValue(g, "Payment", _model.PaymentMethod, y);
            return y + 2;
        }

        private void DrawFiscalBlock(Graphics g, float y)
        {
            g.DrawLine(_dashPen, _margin, y, _margin + _usable, y);
            y += 8;

            y = DrawText(g, _model.IsOffline ? "ZIMRA FISCAL RECEIPT (OFFLINE)" : "ZIMRA FISCAL RECEIPT",
                _fontBold, _accentBrush, y, _usable, StringAlignment.Center);

            if (_model.IsOffline)
            {
                y = DrawText(g, "Signed on this till. Queued for ZIMRA sync.", _fontSmall,
                    Brushes.DimGray, y, _usable, StringAlignment.Center);
            }

            if (!string.IsNullOrWhiteSpace(_model.QrCodeUrl))
            {
                try
                {
                    _qrImage ??= CreateImage(_model.QrCodeUrl!);
                    var size = _model.PaperWidthMm <= 58 ? 82f : 110f;
                    g.DrawImage(_qrImage, _margin + (_usable - size) / 2f, y, size, size);
                    y += size + 6;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not render the fiscal QR code: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(_model.VerificationCode))
            {
                y = DrawText(g, "Verification Code", _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
                y = DrawText(g, _model.VerificationCode!, _fontBold, Brushes.Black, y, _usable, StringAlignment.Center);
            }

            if (!string.IsNullOrWhiteSpace(_model.FiscalCode) &&
                !string.Equals(_model.FiscalCode, _model.VerificationCode, StringComparison.Ordinal))
            {
                y = DrawText(g, "Fiscal Code", _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
                y = DrawText(g, _model.FiscalCode!, _fontSmall, Brushes.Black, y, _usable, StringAlignment.Center);
            }

            var counters = new List<string>();
            if (_model.ReceiptNumber is not null) counters.Add($"Receipt: {_model.ReceiptNumber}");
            if (_model.FiscalDayNo is not null) counters.Add($"Day: {_model.FiscalDayNo}");
            if (_model.ReceiptCounter is not null) counters.Add($"Counter: {_model.ReceiptCounter}");
            if (_model.ReceiptGlobalNo is not null) counters.Add($"Global: {_model.ReceiptGlobalNo}");

            if (counters.Count > 0)
            {
                y += 2;
                y = DrawText(g, string.Join("  ", counters), _fontSmall, Brushes.DimGray,
                    y, _usable, StringAlignment.Center);
            }

            y += 6;
            DrawText(g, _model.FooterText, _fontSmall, Brushes.DimGray, y, _usable, StringAlignment.Center);
        }

        private float DrawKeyValue(Graphics g, string key, string value, float y, bool bold = false)
        {
            var font = bold ? _fontBold : _fontRegular;
            var keyHeight = DrawAligned(g, key, font, Brushes.DimGray, _margin, _usable / 2f, StringAlignment.Near, y);
            var valueHeight = DrawAligned(g, value, font, Brushes.Black,
                _margin + _usable / 2f, _usable / 2f, StringAlignment.Far, y);
            return y + Math.Max(keyHeight, valueHeight) + 1;
        }

        private float DrawText(Graphics g, string text, Font font, Brush brush, float y, float width,
            StringAlignment alignment)
        {
            using var format = new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None
            };

            var size = g.MeasureString(text, font, new SizeF(width, 10000f), format);
            g.DrawString(text, font, brush, new RectangleF(_margin, y, width, size.Height + 2), format);
            return y + size.Height;
        }

        private static float DrawAligned(Graphics g, string text, Font font, Brush brush, float x, float width,
            StringAlignment alignment, float y)
        {
            using var format = new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.None
            };

            var size = g.MeasureString(text, font, new SizeF(width, 10000f), format);
            g.DrawString(text, font, brush, new RectangleF(x, y, width, size.Height + 2), format);
            return size.Height;
        }

        private static float LineHeight(Graphics g, Font font) => font.GetHeight(g) + 2;

        private static Image CreateImage(string qrText)
        {
            var png = QrCodeGenerator.CreatePng(qrText);
            using var stream = new MemoryStream(png);
            return Image.FromStream(stream);
        }

        public void Dispose()
        {
            _fontCompany.Dispose();
            _fontBold.Dispose();
            _fontRegular.Dispose();
            _fontSmall.Dispose();
            _accentBrush.Dispose();
            _accentPen.Dispose();
            _dashPen.Dispose();
            _qrImage?.Dispose();
        }
    }
}
