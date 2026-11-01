using System.Diagnostics;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Decoding;

/// <summary>
/// The Ghostscript invocation shared by the text and rasterising devices: safety
/// flags, the bundled-resource search path and the configured timeout.
/// </summary>
internal static class GhostscriptRunner
{
    internal sealed record RunResult(string? Error);

    internal static ProcessStartInfo BuildStartInfo(string executable)
    {
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

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new RunResult($"Ghostscript timed out after {config.GhostscriptTimeoutSeconds}s.");
        }

        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            log.Warn($"Ghostscript exited with code {process.ExitCode}: {stderr.Trim()}");
            return new RunResult($"Ghostscript exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return new RunResult(null);
    }
}
