using System.Drawing;
using System.Drawing.Printing;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor;

/// <summary>
/// Prints the POS's own document and stamps the fiscal proof onto it, so the
/// customer copy keeps the layout the shop already recognises.
///
/// Thermal replays the captured bytes to the till as a RAW spool job — the printer
/// sees exactly what the POS sent — and follows it with the fiscal stamp on the same
/// roll. A4 rasterises the job with Ghostscript and draws the stamp under the page
/// image, which keeps the proof on the invoice instead of a second document.
/// </summary>
public sealed class OriginalInvoicePrinter
{
    private const int RasterDpi = 150;

    private readonly ILog _log;
    private readonly GhostscriptPageRasterizer _rasterizer;

    public OriginalInvoicePrinter(InterceptorConfig config, ILog log)
    {
        _log = log;
        _rasterizer = new GhostscriptPageRasterizer(config, log);
    }

    public void Print(ReceiptPrintModel model)
    {
        var path = model.OriginalJobPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The source print job for {model.InvoiceNumber} is no longer on disk at '{path}'.");
        }

        var bytes = File.ReadAllBytes(path);

        if (model.Format == PrintFormat.A4)
        {
            PrintA4(model, bytes, path);
            return;
        }

        PrintThermal(model, bytes);
    }

    // ─── Thermal ────────────────────────────────────────────────────────────────

    private void PrintThermal(ReceiptPrintModel model, byte[] bytes)
    {
        var printerName = ResolveThermalPrinter(model);

        // The POS's bytes go out untouched, so whatever the POS told the printer
        // (code page, density, formatting) still applies to its own block.
        RawPrinterWriter.Send(printerName, bytes, $"POS invoice {model.InvoiceNumber}");
        _log.Info($"Replayed {bytes.Length} bytes of the original job to '{printerName}'.");

        PrintThermalStamp(model, printerName);
    }

    private static string ResolveThermalPrinter(ReceiptPrintModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.PrinterName))
        {
            return model.PrinterName.Trim();
        }

        // A RAW job needs a queue name; the Windows default printer supplies one.
        var settings = new PrinterSettings();
        if (!settings.IsValid || string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            throw new InvalidOperationException(
                "The original template prints through the till's queue, so printing.printerName must name it.");
        }

        return settings.PrinterName;
    }

    private static void PrintThermalStamp(ReceiptPrintModel model, string printerName)
    {
        var width = (int)Math.Round(model.PaperWidthMm / 25.4 * 100.0);
        var usable = width - 12f;

        using var measureSession = new StampSession(model, usable, centered: true);
        var stampHeight = measureSession.Measure();

        var document = new PrintDocument
        {
            DocumentName = $"Fiscal stamp {model.InvoiceNumber}",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false
        };

        if (!string.IsNullOrWhiteSpace(printerName))
        {
            document.PrinterSettings.PrinterName = printerName;
        }

        document.PrinterSettings.Copies = (short)Math.Clamp(model.Copies, 1, 5);

        // Roll paper has no fixed length, so the page is cut to exactly the stamp
        // plus its own feed; a taller page would leave blank paper after the QR.
        var pageHeight = (int)Math.Ceiling(stampHeight) + 40;
        document.DefaultPageSettings.PaperSize = new PaperSize("FiscalStamp", width, Math.Max(pageHeight, 120));
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        using var session = new StampSession(model, usable, centered: true);
        document.PrintPage += (_, args) =>
        {
            session.Draw(RequireGraphics(args), 6f, 8f);
            args.HasMorePages = false;
        };
        document.Print();
    }

    /// <summary>
    /// The printer callback always supplies a drawing surface, but the nullable
    /// annotation on it predates the checks here.
    /// </summary>
    private static Graphics RequireGraphics(PrintPageEventArgs args)
    {
        return args.Graphics ?? throw new InvalidOperationException("The print job has no drawing surface.");
    }

    // ─── A4 ─────────────────────────────────────────────────────────────────────

    private void PrintA4(ReceiptPrintModel model, byte[] bytes, string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".ps";
        }

        var token = Guid.NewGuid().ToString("N");
        var raster = _rasterizer
            .RasterizeAsync(bytes, extension, token, RasterDpi, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!raster.Success || raster.PageFiles.Count == 0)
        {
            throw new InvalidOperationException($"The original invoice could not be rendered: {raster.Error}");
        }

        try
        {
            var document = new PrintDocument
            {
                DocumentName = $"Fiscal invoice {model.InvoiceNumber}",
                PrintController = new StandardPrintController(),
                OriginAtMargins = false
            };

            var printer = !string.IsNullOrWhiteSpace(model.A4PrinterName)
                ? model.A4PrinterName
                : model.PrinterName;

            if (!string.IsNullOrWhiteSpace(printer))
            {
                document.PrinterSettings.PrinterName = printer.Trim();
            }

            if (!document.PrinterSettings.IsValid)
            {
                throw new InvalidOperationException("No valid A4 printer is available.");
            }

            var pages = raster.PageFiles.ToList();
            using var session = new A4StampSession(model, pages, _log);
            document.PrintPage += (_, args) => session.DrawPage(args);
            document.EndPrint += (_, _) => session.ReleaseImages();
            document.Print();
        }
        finally
        {
            _rasterizer.Cleanup(token);
        }
    }

    private sealed class A4StampSession : IDisposable
    {
        private readonly ReceiptPrintModel _model;
        private readonly IReadOnlyList<string> _pageFiles;
        private readonly ILog _log;
        private readonly List<Image> _images = new();
        private StampSession? _stamp;
        private int _pageIndex;

        public A4StampSession(ReceiptPrintModel model, IReadOnlyList<string> pageFiles, ILog log)
        {
            _model = model;
            _pageFiles = pageFiles;
            _log = log;
        }

        public void DrawPage(PrintPageEventArgs args)
        {
            var g = args.Graphics ?? throw new InvalidOperationException("The print job has no drawing surface.");
            var area = args.MarginBounds;
            var index = _pageIndex;
            var isLast = index == _pageFiles.Count - 1;
            var image = LoadImage(index);

            // The invoice keeps its own page geometry; the stamp goes under it and
            // the page is scaled to fit if the POS filled it right to the bottom.
            var stampReserve = isLast ? StampHeight(g, area.Width) : 0f;
            var available = area.Height - stampReserve;
            var scale = Math.Min((float)area.Width / image.Width, (float)available / image.Height);

            var drawWidth = (int)(image.Width * scale);
            var drawHeight = (int)(image.Height * scale);
            var left = area.Left + (area.Width - drawWidth) / 2;
            g.DrawImage(image, left, area.Top, drawWidth, drawHeight);

            if (isLast)
            {
                StampFor(area.Width).Draw(g, left, area.Top + drawHeight + 10f);
            }

            _pageIndex++;
            args.HasMorePages = _pageIndex < _pageFiles.Count;
        }

        private float StampHeight(Graphics graphics, int usableWidth)
        {
            _stamp ??= new StampSession(_model, usableWidth, centered: false);
            return _stamp.Measure(graphics);
        }

        private StampSession StampFor(int usableWidth)
        {
            _stamp ??= new StampSession(_model, usableWidth, centered: false);
            return _stamp;
        }

        private Image LoadImage(int index)
        {
            while (_images.Count <= index)
            {
                var path = _pageFiles[_images.Count];
                try
                {
                    // Loaded into memory first: Ghostscript's temp files are removed
                    // in the caller's finally, and Image.FromFile locks its file.
                    using var source = new Bitmap(path);
                    _images.Add(new Bitmap(source));
                }
                catch (Exception ex)
                {
                    _log.Error($"Page {_images.Count + 1} of the original invoice could not be loaded", ex);
                    throw new InvalidOperationException($"Page {_images.Count + 1} could not be read.", ex);
                }
            }

            return _images[index];
        }

        public void ReleaseImages()
        {
            foreach (var image in _images)
            {
                image.Dispose();
            }

            _images.Clear();
        }

        public void Dispose()
        {
            ReleaseImages();
            _stamp?.Dispose();
        }
    }

    /// <summary>
    /// Draws the fiscal stamp: heading, the shared <see cref="FiscalStamp"/> lines
    /// and the verification QR under them. Thermal centres the block on the roll;
    /// A4 left-aligns it with the invoice.
    /// </summary>
    private sealed class StampSession : IDisposable
    {
        private readonly ReceiptPrintModel _model;
        private readonly IReadOnlyList<FiscalStampLine> _lines;
        private readonly Font _heading;
        private readonly Font _line;
        private readonly Brush _text;
        private readonly Image? _qr;
        private readonly bool _centered;
        private readonly float _usable;

        public StampSession(ReceiptPrintModel model, float usable, bool centered)
        {
            _model = model;
            _lines = FiscalStamp.Lines(model);
            _usable = usable;
            _centered = centered;
            _heading = new Font("Segoe UI", centered ? 8.5f : 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            _line = new Font("Segoe UI", centered ? 7f : 8f, FontStyle.Regular, GraphicsUnit.Point);
            _text = Brushes.Black;
            _qr = TryLoadQr(model);
        }

        /// <summary>Height in page units, measured with a screen-context Graphics.</summary>
        public float Measure()
        {
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            return Measure(graphics);
        }

        public float Measure(Graphics g)
        {
            return BlockHeight(g, QRSize(g));
        }

        /// <summary>Draws the block and returns the y coordinate below it.</summary>
        public float Draw(Graphics g, float left, float top)
        {
            var qrSize = QRSize(g);
            var lineHeight = _line.GetHeight(g) + 1f;
            var widest = WidestLine(g);

            var x = left;
            if (_centered)
            {
                var content = Math.Max(widest, qrSize);
                x = left + Math.Max(0f, (_usable - content) / 2f);
            }

            var y = top;
            g.DrawString(FiscalStamp.HeadingFor(_model), _heading, _text, x, y);
            y += _heading.GetHeight(g) + 3f;

            foreach (var line in _lines)
            {
                g.DrawString($"{line.Label}: {line.Value}", _line, _text, x, y);
                y += lineHeight;
            }

            if (_qr is not null)
            {
                y += 4f;
                var qrLeft = _centered ? left + Math.Max(0f, (_usable - qrSize) / 2f) : x;
                g.DrawImage(_qr, qrLeft, y, qrSize, qrSize);
                y += qrSize + 3f;

                var verify = FiscalStamp.VerificationUrl(_model);
                if (verify is not null)
                {
                    g.DrawString(verify, _line, _text, x, y);
                    y += lineHeight;
                }
            }

            return y + 6f;
        }

        private float QRSize(Graphics g)
        {
            if (_qr is null)
            {
                return 0f;
            }

            // Thermal rolls are 58-80 mm wide; a QR bigger than half the roll cannot
            // be scanned across the curve of the paper.
            var limit = _centered ? _usable * 0.55f : 240f;
            return Math.Max(90f, Math.Min(limit, _usable));
        }

        private float WidestLine(Graphics g)
        {
            var widest = g.MeasureString(FiscalStamp.HeadingFor(_model), _heading).Width;
            foreach (var line in _lines)
            {
                widest = Math.Max(widest, g.MeasureString($"{line.Label}: {line.Value}", _line).Width);
            }

            return widest;
        }

        private float BlockHeight(Graphics g, float qrSize)
        {
            var lineHeight = _line.GetHeight(g) + 1f;
            var height = _heading.GetHeight(g) + 3f + _lines.Count * lineHeight;
            if (qrSize > 0f)
            {
                height += 4f + qrSize + 3f;
                if (FiscalStamp.VerificationUrl(_model) is not null)
                {
                    height += lineHeight;
                }
            }

            return height + 6f;
        }

        private static Image? TryLoadQr(ReceiptPrintModel model)
        {
            var payload = FiscalStamp.QrPayload(model);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using var stream = new MemoryStream(QrCodeGenerator.CreatePng(payload));
                using var loaded = Image.FromStream(stream);
                return new Bitmap(loaded);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _heading.Dispose();
            _line.Dispose();
            _qr?.Dispose();
        }
    }
}
