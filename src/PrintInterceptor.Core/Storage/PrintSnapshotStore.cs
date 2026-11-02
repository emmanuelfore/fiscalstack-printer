using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;

namespace PrintInterceptor.Core.Storage;

/// <summary>
/// Stores the exact print model of every fiscalised job so an operator can
/// reprint the original fiscal copy later. The ledger keeps the fiscal result;
/// this keeps the rendering inputs (lines, taxes, buyer, QR, counters).
/// Snapshots are kept even when raw job archiving is off, because they are
/// part of the fiscal record, and are pruned with the configured retention.
/// </summary>
public sealed class PrintSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILog _log;
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly FiscalInvoiceArchive _archive;

    public PrintSnapshotStore(InterceptorConfig config, ILog log)
    {
        _log = log;
        _directory = Path.Combine(config.ResolveDataDirectory(), "state", "print-snapshots");
        _retentionDays = config.Storage.RetentionDays;
        _archive = new FiscalInvoiceArchive(config.ResolveDataDirectory(), log);
    }

    public string DirectoryPath => _directory;

    public string FiscalArchiveDirectoryPath => _archive.DirectoryPath;

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    public bool Save(string hash, ReceiptPrintModel model)
    {
        var snapshotSaved = false;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(hash);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(model, JsonOptions));
            File.Move(temp, path, overwrite: true);
            snapshotSaved = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not store the print snapshot for {Short(hash)}", ex);
        }

        // Keep the dated, human-readable fiscal record even if the internal
        // reprint snapshot could not be written (or vice versa).
        var archiveSaved = _archive.Save(hash, model);
        return snapshotSaved && archiveSaved;
    }

    public ReceiptPrintModel? Load(string hash)
    {
        var path = PathFor(hash);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReceiptPrintModel>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not read the print snapshot for {Short(hash)}", ex);
            return null;
        }
    }

    /// <summary>
    /// The document as issued, from the reprint snapshot when it is still there and from the
    /// fiscal archive when retention has pruned it. A day report needs the bucket split of a
    /// receipt it printed months ago, and the archive is the copy that is never deleted.
    /// </summary>
    public ReceiptPrintModel? LoadDurable(string hash) => Load(hash) ?? _archive.LoadByHash(hash);

    public void Prune()
    {
        if (_retentionDays <= 0 || !Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Pruning print snapshots failed: {ex.Message}");
        }
    }

    private string PathFor(string hash) => Path.Combine(_directory, hash + ".json");

    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];
}
