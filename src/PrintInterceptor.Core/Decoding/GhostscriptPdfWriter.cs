using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

public sealed record PdfResult(bool Success, byte[] Pdf, string? Error)
{
    public static PdfResult Failure(string error) => new(false, Array.Empty<byte>(), error);
}

/// <summary>
/// Turns a captured PostScript job into a PDF with Ghostscript's pdfwrite device.
/// A direct print from an A4 POS (Palladium and friends) arrives as PostScript when
/// the virtual printer uses the PS class driver; keeping only its decoded text loses
/// the original layout, which is what the "original" print template stamps. Writing
/// the PDF next to the text also lets an operator open exactly what the till printed.
/// </summary>
public sealed class GhostscriptPdfWriter
{
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private string? _resolvedPath;
    private bool _lookupDone;

    public GhostscriptPdfWriter(InterceptorConfig config, ILog log)
    {
        _config = config;
        _log = log;
    }

    public async Task<PdfResult> ConvertAsync(byte[] data, CancellationToken cancellationToken)
    {
        var executable = ResolveGhostscript();
        if (executable is null)
        {
            return PdfResult.Failure(
                "Ghostscript was not found. A PDF cannot be produced from a PostScript " +
                "print job without it; bundle it in the release or set ghostscriptPath.");
        }

        var workDir = Path.Combine(_config.ResolveDataDirectory(), "tmp");
        Directory.CreateDirectory(workDir);

        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, token + ".ps");
        var outputPath = Path.Combine(workDir, token + ".pdf");

        try
        {
            await File.WriteAllBytesAsync(inputPath, data, cancellationToken);

            var startInfo = GhostscriptRunner.BuildStartInfo(executable);
            startInfo.ArgumentList.Add("-sDEVICE=pdfwrite");
            startInfo.ArgumentList.Add("-dCompatibilityLevel=1.4");
            startInfo.ArgumentList.Add($"-sOutputFile={outputPath}");
            startInfo.ArgumentList.Add(inputPath);

            var run = await GhostscriptRunner.RunAsync(startInfo, _config, _log, cancellationToken);
            if (run.Error is not null)
            {
                return PdfResult.Failure(run.Error);
            }

            if (!File.Exists(outputPath))
            {
                return PdfResult.Failure("Ghostscript produced no PDF output.");
            }

            return new PdfResult(true, await File.ReadAllBytesAsync(outputPath, cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Ghostscript PostScript-to-PDF conversion failed", ex);
            return PdfResult.Failure(ex.Message);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private string? ResolveGhostscript()
    {
        if (_lookupDone)
        {
            return _resolvedPath;
        }

        _lookupDone = true;
        _resolvedPath = GhostscriptLocator.Resolve(_config, _log);
        return _resolvedPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp files are retried by the OS cleanup.
        }
    }
}
