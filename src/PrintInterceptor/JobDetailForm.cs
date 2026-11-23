using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// The drawer behind a job row, borrowed from FiscalBridge's document detail: the ledger's full
/// record including retry and duplicate history, the exact request body that was sent, and the
/// receipt text it was built from. Everything is read from what this till already retained.
/// </summary>
public sealed class JobDetailForm : Form
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly LedgerEntry _entry;
    private readonly JobStore _jobs;
    private readonly PrintSnapshotStore _snapshots;

    public JobDetailForm(LedgerEntry entry, JobStore jobs, PrintSnapshotStore snapshots)
    {
        _entry = entry;
        _jobs = jobs;
        _snapshots = snapshots;

        Text = $"Job {Short(entry.Hash)} — {entry.InvoiceNumber ?? "no invoice number"}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(680, 440);

        var tabs = new TabControl { Dock = DockStyle.Fill, Font = UiTheme.Ui(9f) };
        tabs.TabPages.Add(TextPage("Summary", Summary(entry, snapshots)));
        tabs.TabPages.Add(TextPage("Request sent", PrettyJson(jobs.LoadRequest(entry.SourceFile))
            ?? "(The request body was not retained. Turn storage.retainJobs on to keep it for review and reconciliation.)"));
        tabs.TabPages.Add(TextPage("Receipt text", jobs.LoadText(entry.SourceFile)
            ?? "(The decoded receipt text was not retained.)"));

        var openJob = new Button { Text = "Open stored document", AutoSize = true };
        UiTheme.Secondary(openJob);
        openJob.Enabled = FiscalDocumentActions.CanOpen(entry, snapshots, jobs);
        openJob.Click += (_, _) => OpenSourceJob();
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };
        UiTheme.Secondary(close);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            BackColor = UiTheme.Surface,
            Padding = new Padding(16, 8, 16, 8)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(openJob);

        Controls.Add(tabs);
        Controls.Add(buttons);
        CancelButton = close;
    }

    private static TabPage TextPage(string title, string content)
    {
        var page = new TabPage(title);
        page.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Mono(9f),
            BorderStyle = BorderStyle.None,
            Padding = new Padding(6),
            Text = content
        });
        return page;
    }

    private static string Summary(LedgerEntry entry, PrintSnapshotStore snapshots) => string.Join(Environment.NewLine, new[]
    {
        $"Hash............ {entry.Hash}",
        $"Status.......... {entry.Status}",
        $"Document type... {entry.TransactionType ?? "(predates the sale/credit distinction)"}",
        $"Invoice......... {entry.InvoiceNumber ?? "-"}",
        $"Business date... {entry.BusinessDate}",
        $"Total........... {entry.Currency} {entry.Total:0.00}".Trim(),
        $"Created......... {entry.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
        $"Updated......... {entry.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
        "",
        "Fiscal proof",
        $"Fiscal code..... {OrDash(entry.FiscalCode)}",
        $"Verification.... {OrDash(entry.VerificationCode)}",
        $"QR.............. {OrDash(entry.QrCode)}",
        $"Receipt no...... {entry.ReceiptNumber?.ToString() ?? "-"}",
        $"Day / counter... {entry.FiscalDayNo?.ToString() ?? "-"} / {entry.ReceiptCounter?.ToString() ?? "-"}",
        $"Global no....... {entry.ReceiptGlobalNo?.ToString() ?? "-"}",
        $"Offline origin.. {(entry.OfflineOrigin ? "yes — signed offline" : "no")}",
        $"Synced at....... {(entry.SyncedAt is { } at ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
        "",
        "Retry and duplicate history",
        $"Print status.... {entry.PrintStatus}",
        $"Reprint count... {entry.ReprintCount}",
        $"Duplicates blocked... {entry.DuplicateAttemptCount}",
        $"Last duplicate... {(entry.LastDuplicateAttemptAt is { } duplicateAt ? duplicateAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
        $"Duplicate job... {OrDash(entry.LastDuplicateAttemptHash)}",
        $"Reconcile passes... {entry.ReconcileAttempts}",
        $"Last reconcile... {(entry.LastReconcileAt is { } reconcileAt ? reconcileAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
        "",
        $"Print error..... {OrDash(entry.PrintError)}",
        $"Reason.......... {OrDash(entry.Error)}",
        $"Source job...... {OrDash(entry.SourceFile)}",
        $"Fiscal copy ready... {(snapshots.ExistsDurable(entry.Hash) ? "yes" : "no")}"
    });

    /// <summary>Re-indents the retained request so an operator can read it; falls back to the raw body.</summary>
    private static string? PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private void OpenSourceJob()
    {
        var path = FiscalDocumentActions.OpenablePath(_entry, _snapshots, _jobs);
        if (path is not null)
        {
            ShellOpen.Path(path);
        }
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;
}
