using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Globalization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor;

/// <summary>
/// Renders the A4 fiscal invoice with GDI, mirroring the react-pdf template that
/// FiscalBridge uses for its own invoice PDFs (<c>client/src/components/invoices/pdf-document.tsx</c>).
/// Every metric below is that template's value in points, converted to the printer's
/// 1/100 inch units, so the two documents are the same page rather than an approximation.
/// </summary>
internal static class A4InvoicePrinter
{
    private const int A4Width = 827;   // 595.28 pt at 1/100 inch
    private const int A4Height = 1169; // 841.89 pt at 1/100 inch

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
        // ── Template metrics, in points ─────────────────────────────────────
        // react-pdf lays this page out with Yoga flexbox; the numbers are the
        // stylesheet's, so the printed page matches the downloaded PDF.

        private const float PagePadding = 24f;

        private const float LogoWidth = 108f;
        private const float LogoHeight = 50f;
        private const float HeaderQr = 68f;
        private const float HeaderGapBelow = 10f;
        private const float VerificationLineGap = 1f;

        private const float TitleSize = 16f;
        private const float TitleBlockGap = 12f;
        private const float AccentBarWidth = 60f;
        private const float AccentBarHeight = 2f;
        private const float AccentBarTop = 5f;
        private const float AccentBarBottom = 3f;

        private const float CardWidth = 0.48f;
        private const float CardPadding = 9f;
        private const float PartiesGap = 12f;
        private const float CardRadius = 6f;
        private const float SectionUnderlineGap = 4f;
        private const float SectionTitleGap = 6f;
        private const float PartyNameGap = 2f;
        private const float SellerBodyGap = 10f;
        private const float InfoLineHeight = 1.4f;

        private const float FiscalPadding = 10f;
        private const float FiscalSpine = 3f;
        private const float FiscalRowGap = 4f;
        private const float FiscalLabelWidth = 0.40f;
        private const float FiscalColumnWidth = 0.48f;
        private const float FiscalGap = 16f;

        private const float NoteBandPadding = 5f;
        private const float NoteBandGap = 6f;
        private const float NotePairGap = 10f;

        private const float HeaderPadVertical = 6f;
        private const float PadHorizontal = 4f;
        private const float HeaderRowGap = 3f;
        private const float RowPadVertical = 5f;
        private const float TableGap = 14f;
        private const float DefaultLineHeight = 1.2f;

        private const float SummaryWidth = 0.46f;
        private const float SummaryPadding = 8f;
        private const float SummaryTop = 14f;
        private const float TaxRowGap = 2f;
        private const float TotalsRowGap = 4f;
        private const float GrandTotalTop = 5f;
        private const float GrandTotalPadding = 7f;

        private const float NotesTop = 18f;
        private const float NotesPadding = 10f;
        private const float NotesTitleGap = 4f;

        private const float FooterTop = 22f;
        private const float FooterPadding = 10f;
        private const float FooterLineGap = 3f;

        private readonly ReceiptPrintModel _model;
        private readonly ILog _log;

        private readonly Color _accent;
        private readonly Color _surface = ColorTranslator.FromHtml("#f8fafc");
        private readonly Color _text = ColorTranslator.FromHtml("#0f172a");
        private readonly Color _strong = ColorTranslator.FromHtml("#1e293b");
        private readonly Color _muted = ColorTranslator.FromHtml("#64748b");
        private readonly Color _body = ColorTranslator.FromHtml("#475569");
        private readonly Color _faint = ColorTranslator.FromHtml("#94a3b8");
        private readonly Color _border = ColorTranslator.FromHtml("#dbe3ef");
        private readonly Color _rule = ColorTranslator.FromHtml("#cbd5e1");
        private readonly Color _hair = ColorTranslator.FromHtml("#e2e8f0");
        private readonly Color _hairline = ColorTranslator.FromHtml("#f1f5f9");

        // Arial is the metric twin of the Helvetica the template embeds, and it is
        // the only member of that pair guaranteed on a Windows till.
        private const string FontFamily = "Arial";

        private readonly Font _title = new(FontFamily, TitleSize, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _subtitle = new(FontFamily, 8f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _section = new(FontFamily, 9f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _bodyFont = new(FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _bodyBold = new(FontFamily, 9f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _small = new(FontFamily, 8f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _smallBold = new(FontFamily, 8f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _tiny = new(FontFamily, 7f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _company = new(FontFamily, 10f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _total = new(FontFamily, 12f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _italic = new(FontFamily, 9f, FontStyle.Italic, GraphicsUnit.Point);

        private readonly SolidBrush _textBrush;
        private readonly SolidBrush _strongBrush;
        private readonly SolidBrush _mutedBrush;
        private readonly SolidBrush _bodyBrush;
        private readonly SolidBrush _faintBrush;
        private readonly SolidBrush _accentBrush;
        private readonly SolidBrush _surfaceBrush;
        private readonly Pen _borderPen;
        private readonly Pen _rulePen;
        private readonly Pen _hairPen;
        private readonly Pen _hairlinePen;
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
                _accent = ColorTranslator.FromHtml("#4f46e5");
            }

            _textBrush = new SolidBrush(_text);
            _strongBrush = new SolidBrush(_strong);
            _mutedBrush = new SolidBrush(_muted);
            _bodyBrush = new SolidBrush(_body);
            _faintBrush = new SolidBrush(_faint);
            _accentBrush = new SolidBrush(_accent);
            _surfaceBrush = new SolidBrush(_surface);
            _borderPen = new Pen(_border, 1f);
            _rulePen = new Pen(_rule, 1f);
            _hairPen = new Pen(_hair, 1f);
            _hairlinePen = new Pen(_hairline, 1f);
            _accentPen = new Pen(_accent, Pt(1.5f));

            LoadLogo();
        }

        private static float Pt(float points) => points * 100f / 72f;

        private static float Usable => A4Width - Pt(PagePadding) * 2;

        private static float Left => Pt(PagePadding);

        public void DrawPage(PrintPageEventArgs e)
        {
            var g = e.Graphics
                ?? throw new InvalidOperationException("Print page has no graphics context.");
            g.PageUnit = GraphicsUnit.Display;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var y = Left;
            // Trust the driver's own page height when it reports one; some A4 devices
            // report a trimmed media box and text would be clipped on the last line.
            var pageHeight = e.PageBounds.Height > 0 ? e.PageBounds.Height : A4Height;
            var pageBottom = pageHeight - Pt(PagePadding);

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

            // The summary, notes and footer stay together: react-pdf would split them
            // mid-card, which no customer expects from a fiscal invoice.
            if (y + TailHeight(g) > pageBottom)
            {
                e.HasMorePages = true;
                return;
            }

            y = DrawTaxAndTotals(g, y);
            y = DrawNotes(g, y);
            DrawFooter(g, y);
        }

        // ── Header ─────────────────────────────────────────────────────────

        private float DrawHeader(Graphics g, float y)
        {
            var top = y;
            var rowHeight = Math.Max(Pt(LogoHeight), Pt(HeaderQr));

            if (_logo is not null)
            {
                DrawImageContained(g, _logo, new RectangleF(Left, top, Pt(LogoWidth), Pt(LogoHeight)));
            }

            var centerWidth = Usable * 0.44f;
            var centerX = Left + (Usable - centerWidth) / 2f;

            if (!string.IsNullOrWhiteSpace(_model.VerificationCode))
            {
                var blockHeight = LineHeight(_bodyBold) + Pt(VerificationLineGap) + LineHeight(_tiny);
                var codeY = top + Math.Max(0, (rowHeight - blockHeight) / 2f);
                codeY = DrawSingle(g, $"Verification Code: {_model.VerificationCode}",
                    _bodyBold, _strongBrush, centerX, codeY, centerWidth, StringAlignment.Center)
                    + Pt(VerificationLineGap);

                var verifyUrl = string.IsNullOrWhiteSpace(_model.VerifyUrl)
                    ? "https://receipt.zimra.co.zw"
                    : _model.VerifyUrl!;
                DrawSingle(g, $"Verify at {verifyUrl}", _tiny, Brushes.Blue,
                    centerX, codeY, centerWidth, StringAlignment.Center);
            }

            if (!string.IsNullOrWhiteSpace(_model.QrCodeUrl))
            {
                var qr = Pt(HeaderQr);
                try
                {
                    _qrImage ??= CreateImage(_model.QrCodeUrl!);
                    g.DrawImage(_qrImage, Left + Usable - qr, top + (rowHeight - qr) / 2f, qr, qr);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not render the A4 fiscal QR code: {ex.Message}");
                }
            }

            y = top + rowHeight + Pt(HeaderGapBelow);
            g.DrawLine(_borderPen, Left, y, Left + Usable, y);
            return y + Pt(HeaderGapBelow);
        }

        private float DrawContinuedHeader(Graphics g, float y)
        {
            var title = string.IsNullOrWhiteSpace(_model.CompanyName) ? "FISCAL INVOICE" : _model.CompanyName;
            y = DrawSingle(g, title, _company, _accentBrush, Left, y, Usable, StringAlignment.Center);
            y = DrawSingle(g, $"Invoice {_model.InvoiceNumber} (continued)", _small, _mutedBrush,
                Left, y, Usable, StringAlignment.Center);
            g.DrawLine(_borderPen, Left, y + 2, Left + Usable, y + 2);
            return y + Pt(HeaderGapBelow) + 2;
        }

        private float DrawTitle(Graphics g, float y)
        {
            y = DrawSingle(g, DocumentTitle(), _title, _textBrush, Left, y, Usable, StringAlignment.Center);
            y += Pt(AccentBarTop);

            var barLeft = Left + (Usable - Pt(AccentBarWidth)) / 2f;
            using (var path = RoundedRectangle(barLeft, y, Pt(AccentBarWidth), Pt(AccentBarHeight), Pt(1f)))
            {
                g.FillPath(_accentBrush, path);
            }

            y += Pt(AccentBarHeight) + Pt(AccentBarBottom);

            var subtitle = _model.IsOffline
                ? "ZIMRA fiscal document · signed offline, queued for sync"
                : "ZIMRA fiscal document";

            if (_model.IsReprint)
            {
                subtitle += " · REPRINT COPY";
            }

            y = DrawSingle(g, subtitle, _subtitle, _mutedBrush, Left, y, Usable, StringAlignment.Center);
            return y + Pt(TitleBlockGap);
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
            var cardWidth = Usable * CardWidth;
            var sellerX = Left;
            var buyerX = Left + Usable - cardWidth;
            var textWidth = cardWidth - Pt(CardPadding) * 2;

            var seller = new Party(
                "SELLER",
                _model.CompanyName,
                BodyLines(_model.CompanyAddress, _model.CompanyTin, _model.CompanyVatNumber,
                    phoneFirst: true, _model.CompanyPhone, _model.CompanyEmail),
                null);

            var buyer = new Party(
                "BUYER",
                _model.BuyerName,
                BodyLines(_model.BuyerAddress, _model.BuyerTin, _model.BuyerVatNumber,
                    phoneFirst: false, _model.BuyerPhone, _model.BuyerEmail),
                "Walk-in Customer");

            var sellerHeight = CardHeight(g, seller, textWidth, SellerBodyGap);
            var buyerHeight = CardHeight(g, buyer, textWidth, 0f);
            var height = Math.Max(sellerHeight, buyerHeight);

            DrawPartyCard(g, seller, sellerX, y, cardWidth, height, textWidth, SellerBodyGap);
            DrawPartyCard(g, buyer, buyerX, y, cardWidth, height, textWidth, 0f);

            return y + height + Pt(PartiesGap);
        }

        private sealed record Party(string Title, string? Name, IReadOnlyList<string> Lines, string? WalkInLabel);

        private static List<string> BodyLines(
            string? address,
            string? tin,
            string? vatNumber,
            bool phoneFirst,
            string? phone,
            string? email)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(address))
            {
                lines.Add(address!);
            }

            lines.Add($"TIN: {OrNa(tin)}");
            lines.Add($"VAT No: {OrNa(vatNumber)}");

            if (phoneFirst)
            {
                if (!string.IsNullOrWhiteSpace(phone))
                {
                    lines.Add($"Phone: {phone}");
                }

                if (!string.IsNullOrWhiteSpace(email))
                {
                    lines.Add($"Email: {email}");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(email))
                {
                    lines.Add($"Email: {email}");
                }

                if (!string.IsNullOrWhiteSpace(phone))
                {
                    lines.Add($"Phone: {phone}");
                }
            }

            return lines;
        }

        private float CardHeight(Graphics g, Party party, float textWidth, float extraGap)
        {
            var height = Pt(CardPadding) * 2 + SectionTitleHeight(g) + extraGap;

            if (!string.IsNullOrWhiteSpace(party.Name))
            {
                height += LineHeight(_company) + Pt(PartyNameGap);
            }

            foreach (var line in party.Lines)
            {
                height += Wrapped(g, line, _bodyFont, textWidth).Count * LineHeight(_bodyFont, InfoLineHeight);
            }

            if (party.WalkInLabel is not null && string.IsNullOrWhiteSpace(party.Name))
            {
                height += LineHeight(_bodyFont, InfoLineHeight);
            }

            return height;
        }

        private void DrawPartyCard(
            Graphics g,
            Party party,
            float x,
            float y,
            float width,
            float height,
            float textWidth,
            float extraGap)
        {
            DrawCard(g, x, y, width, height);

            var cursor = y + Pt(CardPadding);
            cursor = DrawSectionTitle(g, party.Title, x + Pt(CardPadding), cursor, textWidth);

            if (!string.IsNullOrWhiteSpace(party.Name))
            {
                cursor = DrawSingle(g, party.Name!, _company, _strongBrush, x + Pt(CardPadding), cursor, textWidth);
                cursor += Pt(PartyNameGap);
                cursor += extraGap;
            }
            else if (party.WalkInLabel is not null)
            {
                DrawSingle(g, party.WalkInLabel, _italic, _faintBrush,
                    x + Pt(CardPadding), cursor + Pt(PartyNameGap), textWidth);
                return;
            }

            foreach (var line in party.Lines)
            {
                foreach (var wrapped in Wrapped(g, line, _bodyFont, textWidth))
                {
                    cursor = DrawSingle(g, wrapped, _bodyFont, _bodyBrush, x + Pt(CardPadding), cursor, textWidth);
                }
            }
        }

        private float SectionTitleHeight(Graphics g) =>
            LineHeight(_section) + Pt(SectionUnderlineGap) + Pt(SectionTitleGap);

        private float DrawSectionTitle(Graphics g, string title, float x, float y, float width)
        {
            var cursor = DrawSingle(g, title, _section, _accentBrush, x, y, width);
            g.DrawLine(_rulePen, x, cursor + Pt(SectionUnderlineGap) - 2, x + width, cursor + Pt(SectionUnderlineGap) - 2);
            return cursor + Pt(SectionUnderlineGap) + Pt(SectionTitleGap);
        }

        // ── Fiscal grid ─────────────────────────────────────────────────────

        private float DrawFiscalGrid(Graphics g, float y)
        {
            var invoiceValue = FiscalStamp.InvoiceNumber(_model);

            var left = new[]
            {
                ("Invoice No:", invoiceValue),
                ("Customer Reference No:", OrNa(_model.InvoiceNumber)),
                ("Fiscal Day No:", _model.FiscalDayNo?.ToString(CultureInfo.InvariantCulture) ?? "N/A")
            };

            var right = new[]
            {
                ("Date:", FiscalStamp.DateOnly(_model.DateText)),
                ("Fiscal Device ID:", OrNa(_model.DeviceId)),
                ("Device Serial No:", OrNa(_model.DeviceSerialNo))
            };

            var rows = Math.Max(left.Length, right.Length);
            var rowHeight = LineHeight(_bodyFont) + Pt(FiscalRowGap);
            var height = Pt(FiscalPadding) * 2 + rows * LineHeight(_bodyFont) + (rows - 1) * Pt(FiscalRowGap);

            DrawCard(g, Left, y, Usable, height);
            DrawFiscalSpine(g, y, height);

            var innerWidth = Usable - Pt(FiscalPadding) * 2 - Pt(FiscalSpine);
            var columnWidth = innerWidth * FiscalColumnWidth;
            var leftX = Left + Pt(FiscalPadding) + Pt(FiscalSpine);
            var rightX = leftX + innerWidth - columnWidth;

            var rowY = y + Pt(FiscalPadding);
            foreach (var (label, value) in left)
            {
                DrawLabelValue(g, label, value, leftX, rowY, columnWidth);
                rowY += rowHeight;
            }

            rowY = y + Pt(FiscalPadding);
            foreach (var (label, value) in right)
            {
                DrawLabelValue(g, label, value, rightX, rowY, columnWidth);
                rowY += rowHeight;
            }

            return y + height + Pt(FiscalGap);
        }

        /// <summary>
        /// The template draws the accent as a 3 pt left border, so it follows the
        /// card's rounded corner instead of standing as a separate bar.
        /// </summary>
        private void DrawFiscalSpine(Graphics g, float y, float height)
        {
            using var path = RoundedRectangle(Left, y, Usable, height, Pt(CardRadius));
            var state = g.Save();
            g.SetClip(path, CombineMode.Replace);
            g.FillRectangle(_accentBrush, Left, y, Pt(FiscalSpine), height);
            g.Restore(state);
        }

        private void DrawLabelValue(Graphics g, string label, string value, float x, float y, float width)
        {
            var labelWidth = width * FiscalLabelWidth;
            DrawSingle(g, label, _bodyFont, _mutedBrush, x, y, labelWidth, wrap: false);
            DrawSingle(g, value, _bodyBold, _strongBrush, x + labelWidth, y, width - labelWidth, wrap: false);
        }

        private bool IsNote() =>
            string.Equals(_model.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_model.TransactionType, "DebitNote", StringComparison.OrdinalIgnoreCase);

        private float DrawRelatedBand(Graphics g, float y)
        {
            if (!IsNote() || string.IsNullOrWhiteSpace(_model.RelatedInvoiceNumber))
            {
                return y;
            }

            var kind = string.Equals(_model.TransactionType, "CreditNote", StringComparison.OrdinalIgnoreCase)
                ? "CREDITED INVOICE"
                : "DEBITED INVOICE";

            var pairs = new List<(string Label, string Value)>
            {
                ("Inv No: ", OrNa(_model.RelatedInvoiceNumber)),
                ("Date: ", OrNa(FiscalStamp.DateOnly(_model.RelatedInvoiceDate))),
                ("Ref: ", OrNa(_model.RelatedInvoiceNumber)),
                ("ID: ", OrNa(_model.DeviceId)),
                ("Serial: ", OrNa(_model.DeviceSerialNo))
            };

            var textWidth = Usable - Pt(NoteBandPadding) * 2;
            y += Pt(NoteBandGap);

            // Caption line plus the reference run, which wraps across the band like the
            // template's flex row.
            var run = LayoutReferenceRun(g, pairs, textWidth);
            var runLines = run.Count == 0 ? 1 : run[^1].Line + 1;
            var height = Pt(NoteBandPadding) * 2 + LineHeight(_tiny) + Pt(3f) +
                         runLines * LineHeight(_tiny);

            g.FillRectangle(_surfaceBrush, Left, y, Usable, height);
            g.DrawLine(_hairPen, Left, y, Left + Usable, y);
            g.DrawLine(_hairPen, Left, y + height, Left + Usable, y + height);

            var cursor = DrawSingle(g, $"{kind}  |  Original Fiscal Reference", _tiny, _bodyBrush,
                Left + Pt(NoteBandPadding), y + Pt(NoteBandPadding), textWidth, wrap: false);
            cursor += Pt(3f);

            using (var format = Typographic())
            {
                foreach (var (line, offsetX, label, value) in run)
                {
                    var x = Left + Pt(NoteBandPadding) + offsetX;
                    var top = cursor + line * LineHeight(_tiny);
                    g.DrawString(label, _tiny, _mutedBrush, x, top, format);
                    g.DrawString(value, _tiny, _strongBrush,
                        x + TextWidth(g, label, _tiny), top, format);
                }
            }

            return y + height;
        }

        /// <summary>
        /// Lays the reference pairs out in one inline run that wraps, the way the
        /// template's flex-wrap row does. The run is planned before it is drawn so the
        /// band's height is known in advance.
        /// </summary>
        private List<(int Line, float X, string Label, string Value)> LayoutReferenceRun(
            Graphics g, IReadOnlyList<(string Label, string Value)> pairs, float width)
        {
            var run = new List<(int Line, float X, string Label, string Value)>();
            var line = 0;
            var x = 0f;

            foreach (var (label, value) in pairs)
            {
                var pairWidth = TextWidth(g, label, _tiny) + TextWidth(g, value, _tiny);
                if (run.Count > 0 && x + pairWidth > width)
                {
                    line++;
                    x = 0f;
                }

                run.Add((line, x, label, value));
                x += pairWidth + Pt(NotePairGap);
            }

            return run;
        }

        // ── Items table ─────────────────────────────────────────────────────

        private float DrawItems(Graphics g, float y, float pageBottom, out bool done)
        {
            if (_nextItem >= _model.Items.Count && _nextItem > 0)
            {
                done = true;
                return y;
            }

            var columns = Columns();
            var widths = ColumnWidths(columns);

            if (_nextItem == 0)
            {
                y = DrawTableHeader(g, y, columns, widths);
            }

            var firstRowY = y;

            while (_nextItem < _model.Items.Count)
            {
                var item = _model.Items[_nextItem];
                var descriptionWidth = widths[DescriptionIndex(columns)] - Pt(PadHorizontal) * 2;
                var lines = Wrapped(g, item.Name, _bodyFont, descriptionWidth).Count;
                var rowHeight = lines * LineHeight(_bodyFont) + Pt(RowPadVertical) * 2;

                if (y > firstRowY && y + rowHeight > pageBottom - TailHeight(g))
                {
                    done = false;
                    return y;
                }

                if (_nextItem % 2 == 1)
                {
                    g.FillRectangle(_surfaceBrush, Left, y, Usable, rowHeight);
                }

                DrawItemRow(g, item, y, rowHeight, columns, widths);
                g.DrawLine(_borderPen, Left, y + rowHeight, Left + Usable, y + rowHeight);

                y += rowHeight;
                _nextItem++;
            }

            done = true;
            return y + Pt(TableGap);
        }

        private Column[] Columns()
        {
            return _model.TaxInclusive
                ? new[]
                {
                    new Column("Code", 0.10f, StringAlignment.Near),
                    new Column("Description", 0.40f, StringAlignment.Near),
                    new Column("Qty", 0.08f, StringAlignment.Center),
                    new Column("Price", 0.12f, StringAlignment.Far),
                    new Column("VAT", 0.15f, StringAlignment.Far),
                    new Column("Total\n(incl.)", 0.15f, StringAlignment.Far)
                }
                : new[]
                {
                    new Column("Code", 0.08f, StringAlignment.Near),
                    new Column("Description", 0.33f, StringAlignment.Near),
                    new Column("Qty", 0.08f, StringAlignment.Center),
                    new Column("Price", 0.10f, StringAlignment.Far),
                    new Column("Amount\n(excl.)", 0.13f, StringAlignment.Far),
                    new Column("VAT", 0.12f, StringAlignment.Far),
                    new Column("Total\n(incl.)", 0.16f, StringAlignment.Far)
                };
        }

        private static float[] ColumnWidths(Column[] columns)
        {
            var widths = new float[columns.Length];
            var used = 0f;
            for (var index = 0; index < columns.Length; index++)
            {
                widths[index] = index == columns.Length - 1
                    ? Usable - used
                    : (float)Math.Floor(Usable * columns[index].Width);
                used += widths[index];
            }

            return widths;
        }

        private static int DescriptionIndex(Column[] columns) =>
            Array.FindIndex(columns, column => column.Title.StartsWith("Description", StringComparison.Ordinal));

        private float DrawTableHeader(Graphics g, float y, Column[] columns, float[] widths)
        {
            var lines = columns.Max(column => column.Title.Split('\n').Length);
            var height = Pt(HeaderPadVertical) * 2 + lines * LineHeight(_smallBold) + Pt(1f);

            using (var path = RoundedRectangle(Left, y, Usable, height, Pt(CardRadius)))
            {
                g.FillPath(_accentBrush, path);
            }

            var x = Left;
            for (var index = 0; index < columns.Length; index++)
            {
                var cursor = y + Pt(HeaderPadVertical);
                foreach (var line in columns[index].Title.Split('\n'))
                {
                    cursor = DrawSingle(g, line, _smallBold, Brushes.White,
                        x + Pt(PadHorizontal), cursor, widths[index] - Pt(PadHorizontal) * 2,
                        columns[index].Alignment, wrap: false);
                }

                x += widths[index];
            }

            return y + height + Pt(HeaderRowGap);
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
            var vatCell = exempt ? "-" : rate == 0m ? "0.00" : Money(vat);

            var cells = _model.TaxInclusive
                ? new[] { item.Code ?? "0000", item.Name, Qty(item.Quantity), Money(unitPrice), vatCell, Money(totalIncl) }
                : new[] { item.Code ?? "0000", item.Name, Qty(item.Quantity), Money(unitPrice), Money(amountExcl), vatCell, Money(totalIncl) };

            var x = Left;
            var cursor = y + Pt(RowPadVertical);

            for (var index = 0; index < cells.Length; index++)
            {
                var title = columns[index].Title;
                var isDescription = title.StartsWith("Description", StringComparison.Ordinal);
                var isTotal = title.StartsWith("Total", StringComparison.Ordinal);
                var isVat = title == "VAT";

                // The template greys the VAT column only in the inclusive layout; the
                // exclusive one keeps the body colour at 8 pt.
                var brush = index == 0 || (isVat && _model.TaxInclusive) ? _mutedBrush : _textBrush;
                var font = isTotal ? _bodyBold : isVat ? _small : _bodyFont;

                if (isDescription)
                {
                    foreach (var line in Wrapped(g, cells[index], font, widths[index] - Pt(PadHorizontal) * 2))
                    {
                        cursor = DrawSingle(g, line, font, brush,
                            x + Pt(PadHorizontal), cursor, widths[index] - Pt(PadHorizontal) * 2);
                    }
                }
                else
                {
                    DrawSingle(g, cells[index], font, brush,
                        x + Pt(PadHorizontal), cursor, widths[index] - Pt(PadHorizontal) * 2,
                        columns[index].Alignment, wrap: false);
                }

                x += widths[index];
            }
        }

        // ── Tax analysis and totals ─────────────────────────────────────────

        private float TailHeight(Graphics g) =>
            Pt(SummaryTop) + SummaryHeight(g) + NotesHeight(g) + FooterHeight(g) + Pt(FooterTop);

        private float SummaryHeight(Graphics g)
        {
            var taxRows = Pt(SummaryPadding) * 2 + SectionTitleHeight(g) +
                          LineHeight(_small) + Pt(2f) +
                          _model.Taxes.Count * (LineHeight(_small) + Pt(TaxRowGap));

            var totalsRows = Pt(SummaryPadding) * 2 +
                             LineHeight(_bodyFont) + Pt(4f) +
                             3 * (LineHeight(_bodyFont) + Pt(TotalsRowGap)) +
                             Pt(GrandTotalTop) + Pt(GrandTotalPadding) + LineHeight(_total);

            return Math.Max(taxRows, totalsRows);
        }

        private float NotesHeight(Graphics g)
        {
            if (string.IsNullOrWhiteSpace(_model.Notes))
            {
                return 0f;
            }

            var textWidth = Usable - Pt(NotesPadding) * 2;
            var lines = Wrapped(g, _model.Notes!, _bodyFont, textWidth).Count;
            return Pt(NotesPadding) * 2 + LineHeight(_smallBold) + Pt(NotesTitleGap) +
                   lines * LineHeight(_bodyFont) + Pt(NotesTop);
        }

        private float FooterHeight(Graphics g)
        {
            var height = Pt(FooterPadding) + LineHeight(_small) + Pt(FooterLineGap);
            return height + LineHeight(_tiny) + Pt(PagePadding);
        }

        private float DrawTaxAndTotals(Graphics g, float y)
        {
            y += Pt(SummaryTop);

            var cardWidth = Usable * SummaryWidth;
            var taxX = Left;
            var totalsX = Left + Usable - cardWidth;
            var height = SummaryHeight(g);
            var innerWidth = cardWidth - Pt(SummaryPadding) * 2;

            // The template leaves the tax card on the page's own white; only the
            // totals card gets the surface tint.
            DrawCard(g, taxX, y, cardWidth, height, Brushes.White);
            var cursor = DrawSectionTitle(g, "TAX ANALYSIS", taxX + Pt(SummaryPadding), y + Pt(SummaryPadding), innerWidth)
                         - Pt(SectionTitleGap) + Pt(2f);

            var quarters = new[] { 0.25f, 0.25f, 0.25f, 0.25f };
            var headers = new[] { "VAT %", "Net.Amt", "VAT", "Amount" };
            var x = taxX + Pt(SummaryPadding);
            for (var index = 0; index < headers.Length; index++)
            {
                DrawSingle(g, headers[index], _small, _mutedBrush, x, cursor,
                    innerWidth * quarters[index], index == 0 ? StringAlignment.Near : StringAlignment.Far,
                    wrap: false);
                x += innerWidth * quarters[index];
            }

            cursor += LineHeight(_small);
            g.DrawLine(_hairPen, taxX + Pt(SummaryPadding), cursor, taxX + cardWidth - Pt(SummaryPadding), cursor);
            cursor += Pt(2f);

            if (_model.Taxes.Count == 0)
            {
                DrawSingle(g, "-", _small, _mutedBrush, taxX + Pt(SummaryPadding), cursor, innerWidth, wrap: false);
            }

            foreach (var row in _model.Taxes)
            {
                var label = row.IsExempt
                    ? "EXEMPT"
                    : row.Rate is { } rate
                        ? $"{rate.ToString("0.00", CultureInfo.InvariantCulture)}%"
                        : "-";
                var cells = new[]
                {
                    label,
                    Money(row.Net),
                    row.IsExempt ? "-" : row.Rate is null or 0m ? "0.00" : Money(row.Tax),
                    Money(row.Gross)
                };

                x = taxX + Pt(SummaryPadding);
                for (var index = 0; index < cells.Length; index++)
                {
                    DrawSingle(g, cells[index], _small, index == 0 ? _bodyBrush : _textBrush,
                        x, cursor, innerWidth * quarters[index],
                        index == 0 ? StringAlignment.Near : StringAlignment.Far, wrap: false);
                    x += innerWidth * quarters[index];
                }

                cursor += LineHeight(_small) + Pt(TaxRowGap);
            }

            DrawTotalsCard(g, totalsX, y, cardWidth, height, innerWidth);
            return y + height;
        }

        private void DrawTotalsCard(Graphics g, float x, float y, float width, float height, float innerWidth)
        {
            DrawCard(g, x, y, width, height);
            var cursor = y + Pt(SummaryPadding);

            var quantity = _model.Items.Sum(item => item.Quantity);
            DrawSingle(g, "Number of Items", _bodyFont, _mutedBrush,
                x + Pt(SummaryPadding), cursor, innerWidth * 0.6f, wrap: false);
            DrawSingle(g, Qty(quantity), _bodyBold, _strongBrush,
                x + Pt(SummaryPadding) + innerWidth * 0.6f, cursor, innerWidth * 0.4f,
                StringAlignment.Far, wrap: false);

            cursor += LineHeight(_bodyFont);
            g.DrawLine(_hairlinePen, x + Pt(SummaryPadding), cursor, x + width - Pt(SummaryPadding), cursor);
            cursor += Pt(4f);

            var rows = new[]
            {
                ("Total (excl. tax)", Money(_model.Subtotal)),
                ("Total VAT", Money(_model.TaxTotal)),
                ($"Invoice total, {_model.Currency}", Money(_model.Total))
            };

            foreach (var (row, value) in rows)
            {
                DrawSingle(g, row, _bodyFont, _textBrush,
                    x + Pt(SummaryPadding), cursor, innerWidth * 0.6f, wrap: false);
                DrawSingle(g, value, _bodyBold, _textBrush,
                    x + Pt(SummaryPadding) + innerWidth * 0.6f, cursor, innerWidth * 0.4f,
                    StringAlignment.Far, wrap: false);
                cursor += LineHeight(_bodyFont) + Pt(TotalsRowGap);
            }

            cursor += Pt(GrandTotalTop);
            g.DrawLine(_accentPen, x + Pt(SummaryPadding), cursor, x + width - Pt(SummaryPadding), cursor);
            cursor += Pt(GrandTotalPadding);

            var label = string.IsNullOrWhiteSpace(_model.CompanyVatNumber)
                ? "Total amount"
                : "Total amount (incl. tax)";
            DrawSingle(g, label, _total, _textBrush,
                x + Pt(SummaryPadding), cursor, innerWidth * 0.45f, wrap: false);
            DrawSingle(g, $"{_model.Currency} {Money(_model.Total)}", _total, _textBrush,
                x + Pt(SummaryPadding) + innerWidth * 0.45f, cursor, innerWidth * 0.55f,
                StringAlignment.Far, wrap: false);
        }

        // ── Notes and footer ───────────────────────────────────────────────

        private float DrawNotes(Graphics g, float y)
        {
            if (string.IsNullOrWhiteSpace(_model.Notes))
            {
                return y;
            }

            var textWidth = Usable - Pt(NotesPadding) * 2;
            var lines = Wrapped(g, _model.Notes!, _bodyFont, textWidth);
            var height = Pt(NotesPadding) * 2 + LineHeight(_smallBold) + Pt(NotesTitleGap) +
                         lines.Count * LineHeight(_bodyFont);

            y += Pt(NotesTop);
            DrawCard(g, Left, y, Usable, height);

            var title = IsNote() ? "REASON" : "NOTES";
            var cursor = DrawSingle(g, title, _smallBold, _accentBrush,
                Left + Pt(NotesPadding), y + Pt(NotesPadding), textWidth, wrap: false);
            cursor += Pt(NotesTitleGap);

            foreach (var line in lines)
            {
                cursor = DrawSingle(g, line, _bodyFont, _bodyBrush, Left + Pt(NotesPadding), cursor, textWidth);
            }

            return y + height;
        }

        private void DrawFooter(Graphics g, float y)
        {
            y += Pt(FooterTop);
            g.DrawLine(_hairPen, Left, y, Left + Usable, y);
            y += Pt(FooterPadding);

            var contact = string.Join(" | ", new[]
            {
                _model.CompanyName,
                _model.CompanyPhone,
                _model.CompanyEmail
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (contact.Length > 0)
            {
                y = DrawSingle(g, contact, _small, _mutedBrush, Left, y, Usable, StringAlignment.Center, wrap: false);
                y += Pt(FooterLineGap);
            }

            var footer = string.IsNullOrWhiteSpace(_model.FooterText)
                ? "Thank you for your business."
                : _model.FooterText;
            if (_model.IsOffline)
            {
                footer += "  ·  Offline fiscal receipt pending ZIMRA sync.";
            }

            DrawSingle(g, footer, _tiny, _faintBrush, Left, y, Usable, StringAlignment.Center, wrap: false);
        }

        // ── Primitives ─────────────────────────────────────────────────────

        private void DrawCard(Graphics g, float x, float y, float width, float height)
            => DrawCard(g, x, y, width, height, _surfaceBrush);

        private void DrawCard(Graphics g, float x, float y, float width, float height, Brush fill)
        {
            using var path = RoundedRectangle(x, y, width, height, Pt(CardRadius));
            g.FillPath(fill, path);
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

        /// <summary>
        /// react-pdf's Yoga box model uses the font's own line box, so the leading
        /// is a multiple of the size rather than GDI+'s padded measurement.
        /// </summary>
        private static float LineHeight(Font font, float multiple = DefaultLineHeight) =>
            Pt(font.SizeInPoints * multiple);

        private static float TextWidth(Graphics g, string text, Font font)
        {
            using var format = Typographic();
            return g.MeasureString(text, font, PointF.Empty, format).Width;
        }

        private static List<string> Wrapped(Graphics g, string text, Font font, float width)
        {
            var lines = new List<string>();
            var available = Math.Max(width, 1f);

            foreach (var paragraph in text.Split('\n'))
            {
                var current = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && TextWidth(g, candidate, font) > available)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }

                lines.Add(current);
            }

            return lines;
        }

        private static StringFormat Typographic() =>
            new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.LineLimit };

        private static float DrawSingle(
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
            var lines = wrap
                ? Wrapped(g, text, font, width)
                : new List<string> { Ellipsis(g, text, font, width) };

            using var format = Typographic();
            var cursor = y;
            foreach (var line in lines)
            {
                var left = alignment switch
                {
                    StringAlignment.Center => x + Math.Max(0f, (width - TextWidth(g, line, font)) / 2f),
                    StringAlignment.Far => x + width - TextWidth(g, line, font),
                    _ => x
                };

                g.DrawString(line, font, brush, left, cursor, format);
                cursor += LineHeight(font);
            }

            return cursor;
        }

        /// <summary>
        /// GDI+ has no ellipsis helper for the typographic format, so a value that
        /// would run into the next column is trimmed by measurement instead.
        /// </summary>
        private static string Ellipsis(Graphics g, string text, Font font, float width)
        {
            if (TextWidth(g, text, font) <= width)
            {
                return text;
            }

            var drawn = text;
            while (drawn.Length > 1 && TextWidth(g, drawn + "…", font) > width)
            {
                drawn = drawn[..^1];
            }

            return drawn + "…";
        }

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
            _italic.Dispose();
            _textBrush.Dispose();
            _strongBrush.Dispose();
            _mutedBrush.Dispose();
            _bodyBrush.Dispose();
            _faintBrush.Dispose();
            _accentBrush.Dispose();
            _surfaceBrush.Dispose();
            _borderPen.Dispose();
            _rulePen.Dispose();
            _hairPen.Dispose();
            _hairlinePen.Dispose();
            _accentPen.Dispose();
            _qrImage?.Dispose();
            _logo?.Dispose();
        }

        private readonly record struct Column(string Title, float Width, StringAlignment Alignment);
    }
}
