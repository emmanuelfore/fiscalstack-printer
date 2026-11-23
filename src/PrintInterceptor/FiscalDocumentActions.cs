using System.Text.RegularExpressions;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// Row-level document actions shared by the jobs worklist and job detail view. There is no
/// gateway PDF endpoint and no PDF copy is stored with the job, so "Save PDF" renders the
/// permanent fiscal archive through the same A4 layout used for printing. The fiscal fields,
/// QR payload and totals are untouched.
/// </summary>
internal static class FiscalDocumentActions
{
    private static readonly Regex InvalidFileName = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    public static string? OpenablePath(LedgerEntry entry, PrintSnapshotStore snapshots, JobStore jobs)
    {
        var original = jobs.RetainedOriginalPath(entry.SourceFile);
        if (original is not null)
        {
            return original;
        }

        return snapshots.FiscalTextPath(entry.Hash);
    }

    public static bool CanOpen(LedgerEntry entry, PrintSnapshotStore snapshots, JobStore jobs) =>
        OpenablePath(entry, snapshots, jobs) is not null;

    public static bool CanSavePdf(LedgerEntry entry, PrintSnapshotStore snapshots) =>
        snapshots.ExistsDurable(entry.Hash);

    /// <summary>
    /// Loads the archived fiscal document for an in-app preview. The reprint stamp is cleared
    /// only in the returned copy; a PDF export or print still shows REPRINT as required.
    /// </summary>
    public static bool TryLoadModel(
        LedgerEntry entry,
        PrintSnapshotStore snapshots,
        out ReceiptPrintModel? model,
        out string error)
    {
        model = snapshots.LoadDurable(entry.Hash);
        if (model is null)
        {
            error = "No fiscal document was archived for this job, so there is nothing to preview.";
            return false;
        }

        model.IsReprint = false;
        error = "";
        return true;
    }

    public static bool TrySavePdf(
        LedgerEntry entry,
        PrintSnapshotStore snapshots,
        ILog log,
        out string savedPath,
        out string error)
    {
        savedPath = "";
        var model = snapshots.LoadDurable(entry.Hash);
        if (model is null)
        {
            error = "No fiscal document was archived for this job, so there is nothing to export.";
            return false;
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);

        var baseName = FileName(entry);
        var target = Path.Combine(downloads, baseName + ".pdf");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(downloads, $"{baseName} ({i}).pdf");
        }

        var wasReprint = model.IsReprint;
        model.IsReprint = false;
        try
        {
            A4InvoicePrinter.ExportPdf(model, target, log);
            savedPath = target;
            error = "";
            log.Info($"Exported a PDF copy of {SafeHash(entry.Hash)} to {target}.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Error($"Could not export a PDF copy of {SafeHash(entry.Hash)}", ex);
            return false;
        }
        finally
        {
            model.IsReprint = wasReprint;
        }
    }

    private static string FileName(LedgerEntry entry)
    {
        var invoice = InvalidFileName.Replace(entry.InvoiceNumber ?? "", "_").Trim();
        if (invoice.Length == 0)
        {
            invoice = "FiscalInvoice";
        }

        var date = string.IsNullOrWhiteSpace(entry.BusinessDate)
            ? DateTime.Now.ToString("yyyy-MM-dd")
            : entry.BusinessDate;
        return $"FiscalStack-{invoice}-{date}-{SafeHash(entry.Hash)}";
    }

    private static string SafeHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];
}
