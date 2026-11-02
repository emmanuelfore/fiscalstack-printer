using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// Operator view of every fiscalisation attempt: status, reason, print result
/// and an explicit reprint of the stored fiscal copy.
/// </summary>
public sealed class JobsForm : Form
{
    private readonly JobLedger _ledger;
    private readonly PrintSnapshotStore _snapshots;
    private readonly PrintJobPipeline _pipeline;
    private readonly ILog _log;

    private readonly ListView _list;
    private readonly ComboBox _filter;
    private readonly TextBox _details;
    private readonly Button _reprint;
    private readonly Button _openJob;
    private readonly Label _count;

    public JobsForm(JobLedger ledger, PrintSnapshotStore snapshots, PrintJobPipeline pipeline, ILog log)
    {
        _ledger = ledger;
        _snapshots = snapshots;
        _pipeline = pipeline;
        _log = log;

        Text = "Fiscalisation jobs";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(760, 480);

        _filter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 190
        };
        _filter.Items.AddRange(new object[]
        {
            "All jobs",
            "Needs review",
            "Rejected",
            "Unknown outcome",
            "Fiscalised",
            "Printing problems"
        });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => RefreshList();

        _count = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(12, 8, 3, 3)
        };

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => RefreshList();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 4)
        };
        toolbar.Controls.Add(new Label { Text = "Show:", AutoSize = true, Margin = new Padding(3, 8, 6, 3) });
        toolbar.Controls.Add(_filter);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_count);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false
        };
        _list.Columns.Add("Updated", 135);
        _list.Columns.Add("Invoice", 160);
        _list.Columns.Add("Status", 130);
        _list.Columns.Add("Total", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Print", 110);
        _list.Columns.Add("Detail", 400);
        _list.SelectedIndexChanged += (_, _) => ShowSelection();
        _list.DoubleClick += (_, _) => ShowSelectedDetails();
        _list.Resize += (_, _) => ResizeColumns();

        _details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 250, 252),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle
        };

        var detailsHost = new GroupBox
        {
            Text = "Job detail",
            Dock = DockStyle.Bottom,
            Height = 170,
            Padding = new Padding(8)
        };
        detailsHost.Controls.Add(_details);

        _reprint = new Button { Text = "Reprint fiscal copy", Width = 150, Enabled = false };
        _reprint.Click += (_, _) => ReprintSelected();
        _openJob = new Button { Text = "Open source job", Width = 130, Enabled = false };
        _openJob.Click += (_, _) => OpenSourceJob();
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(10)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_openJob);
        buttons.Controls.Add(_reprint);

        Controls.Add(_list);
        Controls.Add(detailsHost);
        Controls.Add(buttons);
        Controls.Add(toolbar);

        CancelButton = close;
        RefreshList();
    }

    private void RefreshList()
    {
        var selectedHash = SelectedEntry()?.Hash;
        var entries = _ledger.All();
        var filter = _filter.SelectedIndex;

        _list.BeginUpdate();
        _list.Items.Clear();

        var shown = 0;
        foreach (var entry in entries)
        {
            if (!Matches(entry, filter))
            {
                continue;
            }

            var item = new ListViewItem(entry.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
            {
                Tag = entry,
                ForeColor = StatusColor(entry.Status)
            };
            item.SubItems.Add(entry.InvoiceNumber ?? "-");
            item.SubItems.Add(StatusText(entry.Status));
            item.SubItems.Add(string.IsNullOrWhiteSpace(entry.Currency)
                ? entry.Total.ToString("0.00")
                : $"{entry.Currency} {entry.Total:0.00}");
            item.SubItems.Add(PrintText(entry));
            item.SubItems.Add(ShortDetail(entry));
            _list.Items.Add(item);
            shown++;
        }

        _list.EndUpdate();
        _count.Text = $"{shown} of {entries.Count} job(s)";
        ResizeColumns();

        if (selectedHash is not null)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is LedgerEntry entry &&
                    string.Equals(entry.Hash, selectedHash, StringComparison.Ordinal))
                {
                    item.Selected = true;
                    item.Focused = true;
                    _list.EnsureVisible(item.Index);
                    break;
                }
            }
        }

        ShowSelection();
    }

    private static bool Matches(LedgerEntry entry, int filter) => filter switch
    {
        1 => entry.Status == LedgerStatus.NeedsReview,
        2 => entry.Status == LedgerStatus.Rejected,
        3 => entry.Status == LedgerStatus.AmbiguousFailure,
        4 => entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline,
        5 => entry.PrintStatus is "Failed" or "ReprintFailed" ||
             (entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline &&
              entry.PrintStatus is not "Printed" and not "NotRequested" and not "Reprinted"),
        _ => true
    };

    private static Color StatusColor(LedgerStatus status) => status switch
    {
        LedgerStatus.Rejected or LedgerStatus.AmbiguousFailure => Color.FromArgb(185, 28, 28),
        LedgerStatus.NeedsReview or LedgerStatus.InFlight => Color.FromArgb(180, 83, 9),
        LedgerStatus.FiscalisedOffline => Color.FromArgb(29, 78, 216),
        _ => Color.FromArgb(15, 23, 42)
    };

    private static string StatusText(LedgerStatus status) => status switch
    {
        LedgerStatus.InFlight => "In flight",
        LedgerStatus.Fiscalised => "Fiscalised",
        LedgerStatus.FiscalisedOffline => "Offline — queued",
        LedgerStatus.Rejected => "Rejected",
        LedgerStatus.NeedsReview => "Needs review",
        LedgerStatus.AmbiguousFailure => "Unknown outcome",
        _ => status.ToString()
    };

    private static string PrintText(LedgerEntry entry) => entry.PrintStatus switch
    {
        "Printed" => "Printed",
        "Reprinted" => $"Reprinted ({entry.ReprintCount})",
        "ReprintFailed" => "Reprint failed",
        "Failed" => "Failed",
        "Printing" => "Printing",
        "NotRequested" => "-",
        _ => entry.PrintStatus
    };

    private static string ShortDetail(LedgerEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            return entry.Error!;
        }

        if (!string.IsNullOrWhiteSpace(entry.PrintError))
        {
            return entry.PrintError!;
        }

        return entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline
            ? "Fiscal proof recorded"
            : "";
    }

    private LedgerEntry? SelectedEntry() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as LedgerEntry : null;

    private void ShowSelection()
    {
        var entry = SelectedEntry();
        _reprint.Enabled = entry is not null &&
                           entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline &&
                           _snapshots.Exists(entry.Hash);
        _openJob.Enabled = entry?.SourceFile is { Length: > 0 } file && File.Exists(file);
        _details.Text = entry is null ? "" : Describe(entry);
    }

    private void ShowSelectedDetails()
    {
        var entry = SelectedEntry();
        if (entry is not null)
        {
            MessageBox.Show(Describe(entry), $"Job {entry.Hash[..Math.Min(12, entry.Hash.Length)]}",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private string Describe(LedgerEntry entry)
    {
        var snapshot = _snapshots.Exists(entry.Hash) ? "yes" : "no";
        return string.Join(Environment.NewLine, new[]
        {
            $"Hash............ {entry.Hash}",
            $"Status.......... {StatusText(entry.Status)}",
            $"Invoice......... {entry.InvoiceNumber ?? "-"}",
            $"Business date... {entry.BusinessDate}",
            $"Total........... {entry.Currency} {entry.Total:0.00}".Trim(),
            $"Created......... {entry.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
            $"Updated......... {entry.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
            "",
            $"Fiscal code..... {OrDash(entry.FiscalCode)}",
            $"Verification.... {OrDash(entry.VerificationCode)}",
            $"QR.............. {OrDash(entry.QrCode)}",
            $"Receipt no...... {entry.ReceiptNumber?.ToString() ?? "-"}",
            $"Day / counter... {entry.FiscalDayNo?.ToString() ?? "-"} / {entry.ReceiptCounter?.ToString() ?? "-"}",
            $"Global no....... {entry.ReceiptGlobalNo?.ToString() ?? "-"}",
            $"Synced at....... {(entry.SyncedAt is { } at ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
            "",
            $"Print status.... {PrintText(entry)}",
            $"Reprint count... {entry.ReprintCount}",
            $"Duplicates blocked... {entry.DuplicateAttemptCount}",
            $"Last duplicate... {(entry.LastDuplicateAttemptAt is { } duplicateAt ? duplicateAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
            $"Duplicate job... {OrDash(entry.LastDuplicateAttemptHash)}",
            $"Reconcile passes... {entry.ReconcileAttempts}",
            $"Last reconcile... {(entry.LastReconcileAt is { } reconcileAt ? reconcileAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")}",
            $"Print error..... {OrDash(entry.PrintError)}",
            $"Reason.......... {OrDash(entry.Error)}",
            $"Source job...... {OrDash(entry.SourceFile)}",
            $"Reprint ready... {snapshot}"
        });
    }

    private void ReprintSelected()
    {
        var entry = SelectedEntry();
        if (entry is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Reprint the fiscal copy for {entry.InvoiceNumber ?? "this invoice"}?" +
            Environment.NewLine + Environment.NewLine +
            "The copy is marked REPRINT. The receipt is not fiscalised again.",
            "Reprint fiscal copy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            if (_pipeline.TryReprint(entry.Hash, out var error))
            {
                MessageBox.Show("The fiscal copy was sent to the printer.", "Reprint",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(error, "Could not reprint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Reprinting from the jobs window failed", ex);
            MessageBox.Show(ex.Message, "Could not reprint", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        RefreshList();
    }

    private void OpenSourceJob()
    {
        var entry = SelectedEntry();
        if (entry?.SourceFile is not { Length: > 0 } file || !File.Exists(file))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PrintInterceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResizeColumns()
    {
        if (_list.Columns.Count < 6)
        {
            return;
        }

        var fixedWidth = 0;
        for (var index = 0; index < 5; index++)
        {
            fixedWidth += _list.Columns[index].Width;
        }

        var detail = Math.Max(160, _list.ClientSize.Width - fixedWidth - 24);
        _list.Columns[5].Width = detail;
    }

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;
}
