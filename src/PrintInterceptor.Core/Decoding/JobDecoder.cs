using System.Text;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

public sealed class JobDecoder
{
    // Windows drivers wrap PostScript/PDF in a PJL envelope. The language marker can
    // therefore be a few dozen bytes in, after the UEL and @PJL ENTER LANGUAGE lines.
    private const int PjlScanLimit = 64 * 1024;
    private static ReadOnlySpan<byte> Uel => "\x1B%-12345X"u8;
    private static ReadOnlySpan<byte> PdfMarker => "%PDF-"u8;
    private static ReadOnlySpan<byte> PostScriptMarker => "%!"u8;

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
        var format = DetectFormat(data, out var payloadOffset);
        var payload = Payload(data, payloadOffset);

        if (format is JobFormat.PostScript or JobFormat.Pdf)
        {
            var extension = format == JobFormat.Pdf ? ".pdf" : ".ps";
            var extracted = await _ghostscript.ExtractTextAsync(payload, extension, cancellationToken);
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
                var converted = await _pdfWriter.ConvertAsync(payload, cancellationToken);
                if (converted.Success)
                {
                    pdf = converted.Pdf;
                }
                else
                {
                    _log.Warn($"Could not convert the PostScript print job to PDF: {converted.Error}");
                }
            }

            var title = format == JobFormat.PostScript ? ExtractPostScriptTitle(payload) : null;
            return new DecodedJob(true, format, text, title, null, pdf);
        }

        var decoded = TextSanitizer.Sanitize(TextSanitizer.Decode(data));
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return DecodedJob.Failure(format, "The print job contained no text.");
        }

        return new DecodedJob(true, format, decoded, null, null);
    }

    public static JobFormat DetectFormat(ReadOnlySpan<byte> data) => DetectFormat(data, out _);

    private static JobFormat DetectFormat(ReadOnlySpan<byte> data, out int payloadOffset)
    {
        payloadOffset = 0;
        var offset = SkipWhitespace(data, 0);

        if (data[offset..].StartsWith(PdfMarker))
        {
            return JobFormat.Pdf;
        }

        if (data[offset..].StartsWith(PostScriptMarker))
        {
            return JobFormat.PostScript;
        }

        // A PJL envelope is standard on Windows PostScript queues. Only trust a
        // marker further into the stream when the bytes before it really are PJL;
        // a text receipt containing "%!" must stay a text job.
        var prefix = data[..Math.Min(data.Length, PjlScanLimit)];
        var pdfIndex = prefix.IndexOf(PdfMarker);
        var psIndex = prefix.IndexOf(PostScriptMarker);

        int markerIndex;
        JobFormat format;
        if (pdfIndex >= 0 && (psIndex < 0 || pdfIndex < psIndex))
        {
            markerIndex = pdfIndex;
            format = JobFormat.Pdf;
        }
        else if (psIndex >= 0)
        {
            markerIndex = psIndex;
            format = JobFormat.PostScript;
        }
        else
        {
            return JobFormat.Text;
        }

        if (!LooksLikePjl(prefix[..markerIndex]))
        {
            return JobFormat.Text;
        }

        payloadOffset = markerIndex;
        return format;
    }

    private static byte[] Payload(byte[] data, int offset)
    {
        if (offset <= 0)
        {
            return data;
        }

        var payload = data.AsSpan(offset);
        var end = payload.Length;
        while (end > 0 && IsWhitespace(payload[end - 1]))
        {
            end--;
        }

        // The PJL universal exit language often trails the document. Ghostscript
        // would tolerate it, but it does not belong in the archived payload.
        if (end >= Uel.Length && payload.Slice(end - Uel.Length, Uel.Length).SequenceEqual(Uel))
        {
            end -= Uel.Length;
            while (end > 0 && IsWhitespace(payload[end - 1]))
            {
                end--;
            }
        }

        return payload[..end].ToArray();
    }

    private static bool LooksLikePjl(ReadOnlySpan<byte> prefix) =>
        prefix.IndexOf("@PJL"u8) >= 0 || prefix.IndexOf(Uel) >= 0;

    private static int SkipWhitespace(ReadOnlySpan<byte> data, int offset)
    {
        while (offset < data.Length && IsWhitespace(data[offset]))
        {
            offset++;
        }

        return offset;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f';

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
