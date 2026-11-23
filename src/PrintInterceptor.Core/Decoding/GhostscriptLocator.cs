using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

/// <summary>
/// Finds a usable Ghostscript executable and the resource folders that belong to
/// it. Shared by text extraction and page rasterising so both routes resolve the
/// copy the same way.
///
/// Only bundled copies and OS-managed install folders are trusted. A configured
/// path, a PATH entry or a symlink that resolves into a user-writable folder is
/// rejected: Ghostscript parses untrusted print jobs and must not be replaceable
/// by whoever last wrote to a temp or profile directory.
/// </summary>
public static class GhostscriptLocator
{
    private static readonly string[] ExecutableNames = OperatingSystem.IsWindows()
        ? new[] { "gswin64c.exe", "gswin32c.exe" }
        : new[] { "gs" };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Configured trusted path (for installers), then the bundled copy, then a
    /// trusted Program Files install (or OS folders on Linux), then trusted PATH
    /// entries. A configured path that was rejected or no longer exists falls
    /// through instead of failing, because installers write absolute paths that
    /// outlive the layout they came from.
    /// </summary>
    public static string? Resolve(InterceptorConfig config, ILog? log = null)
    {
        if (!string.IsNullOrWhiteSpace(config.GhostscriptPath))
        {
            if (IsTrustedExecutable(config.GhostscriptPath))
            {
                return Path.GetFullPath(config.GhostscriptPath);
            }

            log?.Error($"Configured ghostscriptPath was rejected: '{config.GhostscriptPath}' " +
                       "is not inside the application folder or a trusted system install.");
        }

        return FindBundled()
            ?? (OperatingSystem.IsWindows() ? FindInProgramFiles() : null)
            ?? FindOnPathTrusted();
    }

    /// <summary>Used by <see cref="GhostscriptRunner"/> as a second gate before Process.Start.</summary>
    internal static bool IsTrustedExecutable(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var full = Path.GetFullPath(path);
            // A symlink or junction in a trusted folder must not point at a
            // user-writable binary, whether the link is on the file or its folder.
            var fileTarget = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            var parent = Path.GetDirectoryName(full);
            var dirTarget = parent is null
                ? null
                : new DirectoryInfo(parent).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            var effective = fileTarget
                ?? (dirTarget is null ? full : Path.Combine(dirTarget, Path.GetFileName(full)));
            return TrustedRoots().Any(root => effective.StartsWith(root, PathComparison));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> TrustedRoots()
    {
        yield return WithSeparator(AppContext.BaseDirectory);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return WithSeparator(root);
                }
            }
        }
        else
        {
            // Root-owned system trees; /usr covers bin, lib and local.
            foreach (var root in new[] { "/usr", "/opt" })
            {
                yield return WithSeparator(root);
            }
        }
    }

    private static string WithSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
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
            if (IsTrustedExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindOnPathTrusted()
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
                    if (IsTrustedExecutable(candidate))
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
                .FirstOrDefault(IsTrustedExecutable);

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
