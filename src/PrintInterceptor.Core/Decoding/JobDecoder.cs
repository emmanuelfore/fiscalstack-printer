using System.Text;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

public sealed class JobDecoder
{
    private readonly GhostscriptTextExtractor _ghostscript;
    private readonly GhostscriptPdfWriter _pdfWriter;
    private readonly ILog _log;

    public JobDecoder(InterceptorConfig config, ILog log)
    {
        _log = log;
        _ghostscript = new GhostscriptTextExtractor(config, log);
        _pdfWriter = new GhostscriptPdfWriter(config, log);
    }

    public async Task<DecodedJob> DecodeAsync(byte[] data, CancellationToken cancellationToken)
    {
        var format = DetectFormat(data);

        if (format is JobFormat.PostScript or JobFormat.Pdf)
        {
            var extension = format == JobFormat.Pdf ? ".pdf" : ".ps";
            var extracted = await _ghostscript.ExtractTextAsync(data, extension, cancellationToken);
            if (!extracted.Success)
            {
                _log.Error($"Could not extract text from {format} job: {extracted.Error}");
                return DecodedJob.Failure(format, extracted.Error ?? "Text extraction failed.");
            }

            var text = TextSanitizer.Sanitize(extracted.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return DecodedJob.Failure(format, "The print job contained no extractable text.");
            }

            // A PDF job is already the file to keep. A PostScript job is converted so the
            // archive holds the invoice as Palladium printed it, not just decoded text.
            // A failed conversion is not fatal: fiscalisation only needs the text.
            byte[]? pdf = null;
            if (format == JobFormat.PostScript)
            {
                var converted = await _pdfWriter.ConvertAsync(data, cancellationToken);
                if (converted.Success)
                {
                    pdf = converted.Pdf;
                }
                else
                {
                    _log.Warn($"Could not convert the PostScript print job to PDF: {converted.Error}");
                }
            }

            var title = format == JobFormat.PostScript ? ExtractPostScriptTitle(data) : null;
            return new DecodedJob(true, format, text, title, null, pdf);
        }

        var decoded = TextSanitizer.Sanitize(TextSanitizer.Decode(data));
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return DecodedJob.Failure(format, "The print job contained no text.");
        }

        return new DecodedJob(true, format, decoded, null, null);
    }

    public static JobFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length && data[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            offset++;
        }

        if (data[offset..].StartsWith("%PDF"u8))
        {
            return JobFormat.Pdf;
        }

        if (data[offset..].StartsWith("%!"u8))
        {
            return JobFormat.PostScript;
        }

        return JobFormat.Text;
    }

    private static string? ExtractPostScriptTitle(byte[] data)
    {
        var prefixLength = Math.Min(data.Length, 8192);
        var prefix = Encoding.Latin1.GetString(data, 0, prefixLength);

        foreach (var key in new[] { "%%Title:", "%%DocumentName:" })
        {
            var match = Regex.Match(prefix, Regex.Escape(key) + @"\s*(.+)");
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }
}
