using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
                "Ghostscript was not found. Install it and set ghostscriptPath, or install the virtual printer with the 'Generic / Text Only' driver.");
        }

        var workDir = Path.Combine(_config.ResolveDataDirectory(), "tmp");
        Directory.CreateDirectory(workDir);

        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(workDir, token + extension);
        var outputPath = Path.Combine(workDir, token + ".txt");

        try
        {
            await File.WriteAllBytesAsync(inputPath, data, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("-dNOPAUSE");
            startInfo.ArgumentList.Add("-dBATCH");
            startInfo.ArgumentList.Add("-dSAFER");
            startInfo.ArgumentList.Add("-sDEVICE=txtwrite");
            startInfo.ArgumentList.Add($"-sOutputFile={outputPath}");
            startInfo.ArgumentList.Add(inputPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start Ghostscript.");

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.GhostscriptTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignored */ }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new GhostscriptResult(false, string.Empty,
                    $"Ghostscript timed out after {_config.GhostscriptTimeoutSeconds}s.");
            }

            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                return new GhostscriptResult(false, string.Empty,
                    $"Ghostscript exited with code {process.ExitCode}: {stderr.Trim()}");
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

        if (!string.IsNullOrWhiteSpace(_config.GhostscriptPath))
        {
            _resolvedPath = File.Exists(_config.GhostscriptPath) ? _config.GhostscriptPath : null;
            if (_resolvedPath is null)
            {
                _log.Error($"Configured ghostscriptPath does not exist: {_config.GhostscriptPath}");
            }

            return _resolvedPath;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "gswin64c.exe", "gswin32c.exe" }
            : new[] { "gs" };

        foreach (var name in names)
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
            {
                _resolvedPath = onPath;
                return _resolvedPath;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            _resolvedPath = FindInProgramFiles();
        }

        return _resolvedPath;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string? FindInProgramFiles()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var gsRoot = Path.Combine(root, "gs");
            if (!Directory.Exists(gsRoot))
            {
                continue;
            }

            var best = Directory.GetDirectories(gsRoot)
                .Select(dir => new
                {
                    Dir = dir,
                    Version = ParseVersion(Path.GetFileName(dir))
                })
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, "bin", "gswin64c.exe"))
                .FirstOrDefault(File.Exists);

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private static Version? ParseVersion(string name)
    {
        var match = Regex.Match(name, @"gs(\d+\.\d+(\.\d+)?)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
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
