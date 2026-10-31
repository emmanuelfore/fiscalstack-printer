using System.Text;
using PrintInterceptor.Core.Decoding;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class TextSanitizerTests
{
    [Fact]
    public void RemovesEscPosSequencesAndNormalizesLineEndings()
    {
        var bytes = Encoding.ASCII.GetBytes("\x1B@Store\r\n\x1B!\x01Item\fEnd\r\n");

        var text = TextSanitizer.Decode(bytes);

        Assert.Equal("Store\nItem\nEnd", text);
    }

    [Fact]
    public void FallsBackToLatin1ForInvalidUtf8()
    {
        var text = TextSanitizer.Decode(new byte[] { 0xE9 });

        Assert.Equal("é", text);
    }

    [Fact]
    public void DetectsTextPostScriptAndPdf()
    {
        Assert.Equal(JobFormat.Text, JobDecoder.DetectFormat(Encoding.ASCII.GetBytes("Hello receipt")));
        Assert.Equal(JobFormat.PostScript, JobDecoder.DetectFormat(Encoding.ASCII.GetBytes("\n%!PS-Adobe-3.0")));
        Assert.Equal(JobFormat.Pdf, JobDecoder.DetectFormat(Encoding.ASCII.GetBytes("%PDF-1.4")));
    }
}
