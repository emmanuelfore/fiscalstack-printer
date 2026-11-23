using System.Diagnostics;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

/// <summary>
/// The Ghostscript invocation shared by the text and rasterising devices: safety
/// flags, the bundled-resource search path, the configured timeout and a second
/// check that the executable is in a trusted folder.
/// </summary>
internal static class GhostscriptRunner
{
    internal sealed record RunResult(string? Error);

    private const int MaxStderrChars = 4000;

    private static readonly Regex SecretPattern = new(
        @"(?i)\b(api[-_ ]?key|token|secret|password)\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:\\|/)[^\s""'<>]+",
        RegexOptions.CultureInvariant);

    internal static ProcessStartInfo BuildStartInfo(string executable)
    {
        if (!GhostscriptLocator.IsTrustedExecutable(executable))
        {
            throw new InvalidOperationException("Refusing to start Ghostscript from an untrusted location.");
        }

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

        var resourcePath = GhostscriptLocator.ResourceSearchPath(executable);
        if (resourcePath is not null)
        {
            // A bundled Ghostscript is not the copy the vendor installer registered
            // in the registry, so point it at its own lib/Resource folders.
            startInfo.ArgumentList.Add($"-I{resourcePath}");
        }

        return startInfo;
    }

    internal static async Task<RunResult> RunAsync(
        ProcessStartInfo startInfo,
        InterceptorConfig config,
        ILog log,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Ghostscript.");

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.GhostscriptTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignored */ }
            _ = stderrTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var message = $"Ghostscript timed out after {config.GhostscriptTimeoutSeconds}s.";
            log.Warn(message);
            return new RunResult(message);
        }

        var stderr = SanitizeStderr(await stderrTask);
        if (process.ExitCode != 0)
        {
            var detail = $"Ghostscript exited with code {process.ExitCode}: {stderr}";
            log.Warn(detail);
            return new RunResult(detail);
        }

        return new RunResult(null);
    }

    /// <summary>
    /// Ghostscript's diagnostics can echo the input path and interpolations from
    /// the print job. Flatten newlines, drop paths and key/value secrets, then cap
    /// the length so one malformed job cannot flood the till's log or the console.
    /// </summary>
    private static string SanitizeStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "(no stderr)";
        }

        var text = stderr.ReplaceLineEndings(" ").Trim();
        text = SecretPattern.Replace(text, "$1=<redacted>");
        text = PathPattern.Replace(text, "<path>");
        return text.Length <= MaxStderrChars
            ? text
            : text[..MaxStderrChars] + "... (truncated)";
    }
}
