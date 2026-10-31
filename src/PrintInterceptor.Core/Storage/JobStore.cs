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
