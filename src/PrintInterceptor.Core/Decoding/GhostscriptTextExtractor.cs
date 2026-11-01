using System.Diagnostics;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

public sealed record GhostscriptResult(bool Success, string Text, string? Error);

/// <summary>
/// Converts PostScript/PDF print jobs to plain text with Ghostscript's txtwrite
/// device. Used only when the POS prints through a PostScript driver.
/// </summary>
public sealed class GhostscriptTextExtractor
{
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private string? _resolvedPath;
    private bool _lookupDone;

    public GhostscriptTextExtractor(InterceptorConfig config, ILog log)
    {
        _config = config;
        _log = log;
    }

    public async Task<GhostscriptResult> ExtractTextAsync(
        byte[] data,
        string extension,
        CancellationToken cancellationToken)
    {
        var executable = ResolveGhostscript();
        if (executable is null)
        {
            return new GhostscriptResult(false, string.Empty,
                "Ghostscript was not found. Install it and set ghostscriptPath, bundle it in the release, " +
                "or install the virtual printer with the 'Generic / Text Only' driver.");
        }

        var workDir = Path.Combine(_config.ResolveDataDirectory(), "tmp");
        Directory.CreateDirectory(workDir);

        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, token + extension);
        var outputPath = Path.Combine(workDir, token + ".txt");

        try
        {
            await File.WriteAllBytesAsync(inputPath, data, cancellationToken);

            var startInfo = GhostscriptRunner.BuildStartInfo(executable);
            startInfo.ArgumentList.Add("-sDEVICE=txtwrite");
            startInfo.ArgumentList.Add(inputPath);

            var run = await GhostscriptRunner.RunAsync(startInfo, _config, _log, cancellationToken);
            if (run.Error is not null)
            {
                return new GhostscriptResult(false, string.Empty, run.Error);
            }

            if (!File.Exists(outputPath))
            {
                return new GhostscriptResult(false, string.Empty, "Ghostscript produced no text output.");
            }

            var text = await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken);
            return new GhostscriptResult(true, text.TrimStart('\ufeff'), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Ghostscript text extraction failed", ex);
            return new GhostscriptResult(false, string.Empty, ex.Message);
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
