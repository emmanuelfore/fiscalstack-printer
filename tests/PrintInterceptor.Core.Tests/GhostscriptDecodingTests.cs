using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// The two Ghostscript devices the capture pipeline relies on. When Ghostscript is
/// not installed (CI on non-Windows images) the tests return early rather than fail:
/// the Windows integration job is where the real executable is validated.
/// </summary>
public sealed class GhostscriptDecodingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pi-gs-" + Guid.NewGuid().ToString("N"));

    private const string PostScriptJob = """
        %!PS-Adobe-3.0
        %%Pages: 1
        %%Page: 1 1
        /Helvetica findfont 12 scalefont setfont
        72 720 moveto
        (Fiscal Tax Invoice) show
        showpage
        %%EOF
        """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private InterceptorConfig Config() => new()
    {
        Storage = new StorageConfig { DataDirectory = _directory }
    };

    [Fact]
    public async Task TxtwriteExtractsTextFromAPostScriptJob()
    {
        var config = Config();
        if (GhostscriptLocator.Resolve(config) is null)
        {
            return;
        }

        var result = await new GhostscriptTextExtractor(config, NullLog.Instance)
            .ExtractTextAsync(Encoding.ASCII.GetBytes(PostScriptJob), ".ps", CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("Fiscal Tax Invoice", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodedPjlWrappedPostScriptJobUsesGhostscript()
    {
        var config = Config();
        if (GhostscriptLocator.Resolve(config) is null)
        {
            return;
        }

        var pjlJob =
            "\x1B%-12345X@PJL COMMENT MSxpsPS\r\n" +
            "@PJL ENTER LANGUAGE=POSTSCRIPT\r\n" +
            PostScriptJob +
            "\r\n\x1B%-12345X";

        var decoded = await new JobDecoder(config, NullLog.Instance)
            .DecodeAsync(Encoding.ASCII.GetBytes(pjlJob), CancellationToken.None);

        Assert.True(decoded.Success, decoded.Error);
        Assert.Equal(JobFormat.PostScript, decoded.Format);
        Assert.Contains("Fiscal Tax Invoice", decoded.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@PJL", decoded.Text, StringComparison.Ordinal);
        Assert.NotNull(decoded.Pdf);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(decoded.Pdf!, 0, 4));
    }

    [Fact]
    public async Task PdfwriteTurnsAPostScriptJobIntoAPdf()
    {
        var config = Config();
        if (GhostscriptLocator.Resolve(config) is null)
        {
            return;
        }

        var result = await new GhostscriptPdfWriter(config, NullLog.Instance)
            .ConvertAsync(Encoding.ASCII.GetBytes(PostScriptJob), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(result.Pdf, 0, 4));
    }

    [Fact]
    public async Task DecodedPostScriptJobCarriesThePdfForArchiving()
    {
        var config = Config();
        if (GhostscriptLocator.Resolve(config) is null)
        {
            return;
        }

        var decoded = await new JobDecoder(config, NullLog.Instance)
            .DecodeAsync(Encoding.ASCII.GetBytes(PostScriptJob), CancellationToken.None);

        Assert.True(decoded.Success, decoded.Error);
        Assert.Equal(JobFormat.PostScript, decoded.Format);
        Assert.Contains("Fiscal Tax Invoice", decoded.Text, StringComparison.Ordinal);
        Assert.NotNull(decoded.Pdf);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(decoded.Pdf!, 0, 4));
    }
}
