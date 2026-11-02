using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;

namespace PrintInterceptor.Core.Storage;

/// <summary>
/// Retains decoded job text (and the raw PostScript/PDF) for audit, review and
/// manual reprinting. Retention is configurable; the ledger is never pruned.
/// </summary>
public sealed class JobStore
{
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly string _jobsRoot;

    public JobStore(InterceptorConfig config, ILog log)
    {
        _config = config;
        _log = log;
        _jobsRoot = Path.Combine(config.ResolveDataDirectory(), "jobs");
    }

    public string DirectoryPath => _jobsRoot;

    public string? Save(string hash, DecodedJob decoded, byte[] raw)
    {
        if (!_config.Storage.RetainJobs)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(_jobsRoot, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);

            var textPath = Path.Combine(directory, hash + ".txt");
            if (!File.Exists(textPath))
            {
                File.WriteAllText(textPath, decoded.Text, new UTF8Encoding(false));
            }

            if (decoded.Format != JobFormat.Text)
            {
                var extension = decoded.Format == JobFormat.Pdf ? ".pdf" : ".ps";
                var rawPath = Path.Combine(directory, hash + extension);
                if (!File.Exists(rawPath))
                {
                    File.WriteAllBytes(rawPath, raw);
                }
            }

            return textPath;
        }
        catch (Exception ex)
        {
            _log.Error("Could not archive the print job", ex);
            return null;
        }
    }

    /// <summary>
    /// Keeps the bytes exactly as the POS sent them, which is what the
    /// original-invoice template replays or renders. Text jobs are stored too: a
    /// decoded .txt has already lost the ESC/POS formatting.
    /// </summary>
    public string? SaveOriginalCopy(string hash, byte[] raw, JobFormat format)
    {
        if (!_config.Storage.RetainJobs)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(_jobsRoot, DateTime.Now.ToString("yyyy-MM-dd"), "original");
            Directory.CreateDirectory(directory);

            var extension = format switch
            {
                JobFormat.Pdf => ".pdf",
                JobFormat.PostScript => ".ps",
                _ => ".bin"
            };

            var path = Path.Combine(directory, hash + extension);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, raw);
            }

            return path;
        }
        catch (Exception ex)
        {
            _log.Error("Could not retain the original print job", ex);
            return null;
        }
    }

    /// <summary>
    /// Keeps the exact body that was sent for a job. A reconciliation has to replay the
    /// request that timed out, not a fresh interpretation of the receipt text: the invoice
    /// number, the lines and the totals the gateway already saw must match.
    /// </summary>
    public string? SaveRequest(string? sourceFile, string json)
    {
        if (!_config.Storage.RetainJobs || string.IsNullOrWhiteSpace(sourceFile))
        {
            return null;
        }

        try
        {
            var path = RequestPath(sourceFile);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            _log.Error("Could not retain the fiscalisation request", ex);
            return null;
        }
    }

    /// <summary>The retained request beside the decoded text it was built from.</summary>
    public static string RequestPath(string sourceFile) =>
        Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? "",
            Path.GetFileNameWithoutExtension(sourceFile) + ".request.json");

    /// <summary>The decoded receipt text as the printer received it, or null if it was not retained.</summary>
    public string? LoadText(string? sourceFile) => Read(sourceFile);

    public string? LoadRequest(string? sourceFile) =>
        string.IsNullOrWhiteSpace(sourceFile) ? null : Read(RequestPath(sourceFile));

    private static string? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The format a job arrived in, read back from the files kept beside its decoded text.
    /// Reconciliation rebuilds a receipt from the archive rather than from a live job, so the
    /// paper it belongs on has to come from disk.
    /// </summary>
    public JobFormat RetainedFormat(string? sourceFile)
    {
        var parts = PartsOf(sourceFile);
        if (parts.DirectoryName is null)
        {
            return JobFormat.Text;
        }

        if (File.Exists(Path.Combine(parts.DirectoryName, parts.Stem + ".pdf")))
        {
            return JobFormat.Pdf;
        }

        return File.Exists(Path.Combine(parts.DirectoryName, parts.Stem + ".ps"))
            ? JobFormat.PostScript
            : JobFormat.Text;
    }

    /// <summary>The bytes exactly as the POS sent them, when they were retained.</summary>
    public string? RetainedOriginalPath(string? sourceFile)
    {
        var parts = PartsOf(sourceFile);
        if (parts.DirectoryName is null)
        {
            return null;
        }

        foreach (var extension in new[] { ".pdf", ".ps", ".bin" })
        {
            var path = Path.Combine(parts.DirectoryName, "original", parts.Stem + extension);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static (string? DirectoryName, string Stem) PartsOf(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
        {
            return (null, "");
        }

        return (Path.GetDirectoryName(Path.GetFullPath(sourceFile)), Path.GetFileNameWithoutExtension(sourceFile));
    }

    /// <summary>
    /// Preserves the original bytes when decoding fails. Without this path an
    /// unreadable or unsupported spool file disappeared without any file history.
    /// </summary>
    public string? SaveUndecodable(byte[] raw, JobFormat format)
    {
        if (!_config.Storage.RetainJobs)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(_jobsRoot, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(directory);
            var extension = format switch
            {
                JobFormat.Pdf => ".pdf",
                JobFormat.PostScript => ".ps",
                _ => ".txt"
            };
            var path = Path.Combine(directory, Hashing.Sha256Hex(raw) + extension);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, raw);
            }

            _log.Info($"Archived undecodable received print file: {path}");
            return path;
        }
        catch (Exception ex)
        {
            _log.Error("Could not archive the undecodable received print file", ex);
            return null;
        }
    }

    public void Prune()
    {
        if (!_config.Storage.RetainJobs || _config.Storage.RetentionDays <= 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_jobsRoot))
            {
                return;
            }

            var cutoff = DateTime.Now.Date.AddDays(-_config.Storage.RetentionDays);
            foreach (var directory in Directory.GetDirectories(_jobsRoot))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                {
                    continue;
                }

                if (date < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                    _log.Info($"Pruned archived print jobs from {date:yyyy-MM-dd}.");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Pruning archived print jobs failed", ex);
        }
    }
}
