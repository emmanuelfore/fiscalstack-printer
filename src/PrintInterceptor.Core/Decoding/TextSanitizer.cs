using System.Text;
using System.Text.RegularExpressions;

namespace PrintInterceptor.Core.Decoding;

public static class TextSanitizer
{
    // ESC/GS/FS command: optional intermediate bytes (0x20-0x2F), a final byte,
    // then at most one parameter byte. \n and \r are excluded so a sequence that
    // ends a line cannot swallow the line break.
    private static readonly Regex EscPosSequence = new(
        @"[\x1B\x1C\x1D](?:[\x20-\x2F]+[\x30-\x7E]?|[\x30-\x7E])[\x00-\x09\x0B\x0C\x0E-\x1F]?",
        RegexOptions.Compiled);

    public static string Decode(byte[] data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(data);
        }
        catch (DecoderFallbackException)
        {
            // Thermal drivers commonly emit the DOS code page. Latin-1 keeps ASCII
            // intact and never throws, which is all the receipt parser needs.
            text = Encoding.Latin1.GetString(data);
        }

        return Sanitize(text);
    }

    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n');
        text = EscPosSequence.Replace(text, string.Empty);

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\t')
            {
                builder.Append(ch);
            }
            else if (ch >= ' ' && ch != '\u007f' && ch != '\ufeff')
            {
                builder.Append(ch);
            }
        }

        var lines = builder.ToString()
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }
}
