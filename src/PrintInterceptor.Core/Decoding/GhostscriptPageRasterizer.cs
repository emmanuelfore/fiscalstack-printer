using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

public sealed record RasterResult(bool Success, IReadOnlyList<string> PageFiles, string? Error);

/// <summary>
/// Renders a PostScript/PDF print job to page images with Ghostscript's png16m
/// device. This is how the original-invoice template gets the POS's own layout:
/// the page is rasterised and the fiscal stamp is drawn onto it before printing.
/// </summary>
public sealed class GhostscriptPageRasterizer
{
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private string? _resolvedPath;
    private bool _lookupDone;

    public GhostscriptPageRasterizer(InterceptorConfig config, ILog log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Writes <c>&lt;hash&gt;1.png</c>, <c>&lt;hash&gt;2.png</c> … into the config
    /// temp folder and returns their paths. The caller owns the files and must
    /// delete them once the page images have been printed.
    /// </summary>
    public async Task<RasterResult> RasterizeAsync(
        byte[] data,
        string extension,
        string hash,
        int dpi,
        CancellationToken cancellationToken)
    {
        var executable = ResolveGhostscript();
        if (executable is null)
        {
            return new RasterResult(false, Array.Empty<string>(),
                "Ghostscript was not found, so the original invoice cannot be rendered. " +
                "Bundle it in the release or set ghostscriptPath.");
        }

        var workDir = Path.Combine(_config.ResolveDataDirectory(), "tmp");
        Directory.CreateDirectory(workDir);

        var inputPath = Path.Combine(workDir, hash + extension);
        var outputPattern = Path.Combine(workDir, hash + "%d.png");

        try
        {
            await File.WriteAllBytesAsync(inputPath, data, cancellationToken);

            var startInfo = GhostscriptRunner.BuildStartInfo(executable);
            startInfo.ArgumentList.Add("-sDEVICE=png16m");
            startInfo.ArgumentList.Add($"-r{Math.Clamp(dpi, 72, 600)}");
            startInfo.ArgumentList.Add("-dTextAlphaBits=4");
            startInfo.ArgumentList.Add("-dGraphicsAlphaBits=4");
            startInfo.ArgumentList.Add($"-sOutputFile={outputPattern}");
            startInfo.ArgumentList.Add(inputPath);

            var run = await GhostscriptRunner.RunAsync(startInfo, _config, _log, cancellationToken);
            if (run.Error is not null)
            {
                return new RasterResult(false, Array.Empty<string>(), run.Error);
            }

            var pages = Directory.GetFiles(workDir, hash + "*.png")
                .OrderBy(path => PageNumber(path, hash))
                .ToList();

            if (pages.Count == 0)
            {
                return new RasterResult(false, Array.Empty<string>(), "Ghostscript produced no page images.");
            }

            return new RasterResult(true, pages, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Ghostscript page rasterising failed", ex);
            return new RasterResult(false, Array.Empty<string>(), ex.Message);
        }
        finally
        {
            TryDelete(inputPath);
        }
    }

    /// <summary>Deletes the page images a previous call left behind.</summary>
    public void Cleanup(string hash)
    {
        var workDir = Path.Combine(_config.ResolveDataDirectory(), "tmp");
        if (!Directory.Exists(workDir))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(workDir, hash + "*.png"))
        {
            TryDelete(path);
        }
    }

    private static int PageNumber(string path, string hash)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = Path.GetFileName(hash);
        var tail = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        return int.TryParse(tail, out var page) ? page : int.MaxValue;
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
