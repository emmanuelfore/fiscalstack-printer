using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

/// <summary>
/// Finds a usable Ghostscript executable and the resource folders that belong to
/// it. Shared by text extraction and page rasterising so both routes resolve the
/// copy the same way.
/// </summary>
public static class GhostscriptLocator
{
    private static readonly string[] ExecutableNames = OperatingSystem.IsWindows()
        ? new[] { "gswin64c.exe", "gswin32c.exe" }
        : new[] { "gs" };

    /// <summary>
    /// Configured path, then a Ghostscript bundled next to the executable
    /// (build-release.ps1 -GhostscriptDir), then PATH, then Program Files. A
    /// configured path that no longer exists falls through instead of failing,
    /// because installers write absolute paths that outlive the layout they came from.
    /// </summary>
    public static string? Resolve(InterceptorConfig config, ILog? log = null)
    {
        if (!string.IsNullOrWhiteSpace(config.GhostscriptPath))
        {
            if (File.Exists(config.GhostscriptPath))
            {
                return config.GhostscriptPath;
            }

            log?.Error($"Configured ghostscriptPath does not exist: {config.GhostscriptPath}. Searching for another copy.");
        }

        return FindBundled() ?? FindOnPath() ?? (OperatingSystem.IsWindows() ? FindInProgramFiles() : null);
    }

    /// <summary>
    /// The <c>-I</c> search list for an executable that lives in a <c>bin</c> folder
    /// beside <c>lib</c> and <c>Resource</c> — true for a bundled release and for a
    /// normal Program Files install. Null when neither folder is there, leaving
    /// Ghostscript on its own defaults.
    /// </summary>
    public static string? ResourceSearchPath(string executable)
    {
        var root = Path.GetDirectoryName(executable) is { } bin ? Path.GetDirectoryName(bin) : null;
        if (root is null)
        {
            return null;
        }

        var found = new[] { "lib", "Resource" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .ToArray();

        return found.Length == 0 ? null : string.Join(';', found) + ";";
    }

    /// <summary>
    /// AppContext.BaseDirectory is used rather than the working directory because
    /// the Windows service runs from System32.
    /// </summary>
    private static string? FindBundled()
    {
        foreach (var name in ExecutableNames)
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "ghostscript", "bin", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in ExecutableNames)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
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
                .SelectMany(x => ExecutableNames.Select(name => Path.Combine(x.Dir, "bin", name)))
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
        var match = System.Text.RegularExpressions.Regex.Match(name, @"gs(\d+\.\d+(\.\d+)?)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }
}
