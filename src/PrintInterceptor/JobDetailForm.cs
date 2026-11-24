using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// The drawer behind a job row: a preview of the archived fiscal copy, the ledger's full record
/// including retry and duplicate history, the exact request body that was sent, and the receipt
/// text it was built from. Everything is read from what this till already retained.
/// </summary>
public sealed class JobDetailForm : Form
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly LedgerEntry _entry;
    private readonly JobStore _jobs;
    private readonly PrintSnapshotStore _snapshots;
    private readonly ILog _log;

    private readonly TabControl _tabs;
    private readonly TabPage _summary;
    private readonly TabPage _preview;

    public JobDetailForm(
        LedgerEntry entry,
        JobStore jobs,
        PrintSnapshotStore snapshots,
        ILog log,
        bool openPreview = false)
    {
        _entry = entry;
        _jobs = jobs;
        _snapshots = snapshots;
        _log = log;

        Text = $"Job {Short(entry.Hash)} — {entry.InvoiceNumber ?? "no invoice number"}";
        UiTheme.UseAppIcon(this);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(760, 520);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Canvas
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(18, 10, 18, 8)
        };
        var title = UiTheme.Heading(UiTheme.SafeText($"Job {Short(entry.Hash)}"));
        title.Dock = DockStyle.Top;
        var subtitle = UiTheme.Subheading(UiTheme.SafeText(
            $"{entry.InvoiceNumber ?? "no invoice number"} · {UiTheme.Status(entry.Status).Text} · " +
            $"{entry.BusinessDate} · {entry.Currency} {entry.Total:0.00}"));
        subtitle.Dock = DockStyle.Top;
        subtitle.AutoSize = false;
        subtitle.Height = 30;
        subtitle.AutoEllipsis = true;
        subtitle.Padding = new Padding(0, 4, 0, 0);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        _tabs = new TabControl { Dock = DockStyle.Fill, Font = UiTheme.Ui(9f), BackColor = UiTheme.Surface };
        _preview = new TabPage("PDF preview") { BackColor = UiTheme.Surface, Padding = new Padding(8) };
        _summary = TextPage("Summary", Summary(entry, snapshots), wordWrap: true);
        _tabs.TabPages.Add(_preview);
        _tabs.TabPages.Add(_summary);
        _tabs.TabPages.Add(TextPage("Request sent", PrettyJson(jobs.LoadRequest(entry.SourceFile))
            ?? "(The request body was not retained. Turn storage.retainJobs on to keep it for review and reconciliation.)"));
        _tabs.TabPages.Add(TextPage("Receipt text", jobs.LoadText(entry.SourceFile)
            ?? "(The decoded receipt text was not retained.)", wordWrap: true));
        BuildPreview(entry, snapshots);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            BackColor = UiTheme.Surface,
            Padding = new Padding(16, 8, 16, 8),
            WrapContents = false
        };
        var close = Action("Close");
        close.DialogResult = DialogResult.Cancel;
        var savePdf = Action("Save PDF copy", primary: true);
        savePdf.Enabled = FiscalDocumentActions.CanSavePdf(entry, snapshots);
        savePdf.Click += (_, _) => SavePdf();
        var openJob = Action("Open stored document");
        openJob.Enabled = FiscalDocumentActions.CanOpen(entry, snapshots, jobs);
        openJob.Click += (_, _) => OpenSourceJob();

        buttons.Controls.Add(close);
        buttons.Controls.Add(savePdf);
        buttons.Controls.Add(openJob);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        CancelButton = close;
        if (openPreview && _preview.Tag is true)
        {
            _tabs.SelectedTab = _preview;
        }
    }

    private static Button Action(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(132, 36),
            Margin = new Padding(6, 0, 6, 0)
        };
        if (primary)
        {
            UiTheme.Primary(button);
        }
        else
        {
            UiTheme.Secondary(button);
        }

        return button;
    }

    private void BuildPreview(LedgerEntry entry, PrintSnapshotStore snapshots)
    {
        if (!FiscalDocumentActions.TryLoadModel(entry, snapshots, out var model, out var error))
        {
            var unavailable = UiTheme.Notice(
                "No fiscal copy to preview",
                error + " The ledger still shows the fiscal proof and the reason this job was held.",
                UiTheme.NeutralBg,
                UiTheme.Neutral);
            unavailable.Dock = DockStyle.Top;
            unavailable.Height = 92;
            _preview.Controls.Add(unavailable);
            _preview.Tag = true; // The tab is still the answer to "Preview PDF": it explains why.
            return;
        }

        try
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = UiTheme.Surface
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var note = new TextBox
            {
                Text =
                    "Rendered from the archived fiscal record. The verification code, QR payload, " +
                    "fiscal codes, totals, tax buckets and taxType are unchanged. Long documents show page 1.",
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Surface,
                Font = UiTheme.Ui(8.5f),
                ForeColor = UiTheme.Muted,
                Padding = new Padding(4, 4, 4, 2)
            };
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiTheme.Canvas,
                Image = A4InvoicePrinter.RenderPreview(model!, _log)
            };
            layout.Controls.Add(note, 0, 0);
            layout.Controls.Add(picture, 0, 1);
            _preview.Controls.Add(layout);
            _preview.Tag = true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not render the fiscal copy preview", ex);
            var failed = UiTheme.Notice(
                "Preview unavailable",
                ex.Message + " Use Save PDF copy or Open stored document instead.",
                UiTheme.WarningBg,
                UiTheme.Warning);
            failed.Dock = DockStyle.Top;
            failed.Height = 92;
            _preview.Controls.Add(failed);
            _preview.Tag = true;
        }
    }

    private static TabPage TextPage(string title, string content, bool wordWrap = false)
    {
        var page = new TabPage(title) { BackColor = UiTheme.Surface, Padding = new Padding(6) };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = wordWrap ? ScrollBars.Vertical : ScrollBars.Both,
            WordWrap = wordWrap,
            Font = UiTheme.Mono(9f),
            Padding = new Padding(8)
        };
        UiTheme.Input(box);
        box.Font = UiTheme.Mono(9f);
        box.Text = content;
        page.Controls.Add(box);
        return page;
    }

    private static string Summary(LedgerEntry entry, PrintSnapshotStore snapshots) => string.Join(Environment.NewLine, new[]
    {
        $"Hash............ {entry.Hash}",
        $"Status.......... {UiTheme.Status(entry.Status).Text}",
        $"Document type... {entry.TransactionType ?? "(predates the sale/credit distinction)"}",
        $"Invoice......... {entry.InvoiceNumber ?? "-"}",
        $"Business date... {entry.BusinessDate}",
        $"Total........... {entry.Currency} {entry.Total:0.00}".Trim(),
        $"Created......... {entry.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
        $"Updated......... {entry.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
        "",
        "Why this needs review",
        $"Stage........... {entry.ReviewStageText}",
        $"Reason.......... {entry.ReviewReasonText}",
        $"Next step....... {entry.ReviewNextStepText}",
        $"Warnings........ {entry.ReviewWarningsText}",
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

    private void SavePdf()
    {
        if (!FiscalDocumentActions.TrySavePdf(_entry, _snapshots, _log, out var path, out var error))
        {
            MessageBox.Show(this, error, "Could not save the PDF copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(this,
            $"Saved the fiscal copy to:{Environment.NewLine}{path}",
            "PDF copy saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
