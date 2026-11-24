using System.Drawing;
using System.Windows.Forms;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// Operator worklist for every fiscalisation attempt. One table shows status, print result and
/// the reason; row actions open the stored source, save a PDF copy or print the fiscal copy
/// without asking the gateway to fiscalise again.
/// </summary>
public sealed class JobsForm : Form
{
    private const int ColUpdated = 0;
    private const int ColInvoice = 1;
    private const int ColStatus = 2;
    private const int ColTotal = 3;
    private const int ColPrint = 4;
    private const int ColDetail = 5;
    private const int ColOpen = 6;
    private const int ColPdf = 7;
    private const int ColReprint = 8;
    private const int ColDelete = 9;

    private readonly JobLedger _ledger;
    private readonly PrintSnapshotStore _snapshots;
    private readonly PrintJobPipeline _pipeline;
    private readonly JobStore _jobs;
    private readonly ILog _log;

    private readonly DataGridView _list;
    private readonly ComboBox _filter;
    private readonly TextBox _search;
    private readonly TextBox _details;
    private readonly CardPanel _reviewNote;
    private readonly TextBox _reviewText;
    private readonly Label _reviewTitle;
    private readonly RowStyle _reviewRow;
    private readonly Button _reprint;
    private readonly Button _openJob;
    private readonly Button _savePdf;
    private readonly Button _previewPdf;
    private readonly Button _deleteLocal;
    private readonly Button _closeButton;
    private readonly Label _count;

    private bool _embeddedMode;

    /// <summary>Hosted by the operator console instead of opened as its own dialog.</summary>
    public bool EmbeddedMode
    {
        get => _embeddedMode;
        set
        {
            _embeddedMode = value;
            if (!value)
            {
                return;
            }

            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            Dock = DockStyle.Fill;
            ShowInTaskbar = false;
            _closeButton.DialogResult = DialogResult.None;
            _closeButton.Text = "Back";
        }
    }

    public event EventHandler? CloseRequested;

    public JobsForm(JobLedger ledger, PrintSnapshotStore snapshots, PrintJobPipeline pipeline, JobStore jobs, ILog log)
    {
        _ledger = ledger;
        _snapshots = snapshots;
        _pipeline = pipeline;
        _jobs = jobs;
        _log = log;

        Text = "Fiscalisation jobs";
        UiTheme.UseAppIcon(this);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(1000, 700);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(0, 0, 0, 8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 270));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(20, 12, 20, 8) };
        var title = UiTheme.Heading("Fiscalisation jobs");
        title.Dock = DockStyle.Top;
        var subtitle = UiTheme.Subheading("Every attempt, its gateway answer and the printed fiscal copy — nothing here fiscalises again.");
        subtitle.Dock = DockStyle.Top;
        subtitle.Padding = new Padding(0, 4, 0, 0);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        _filter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 168,
            Font = UiTheme.Ui(9f)
        };
        _filter.Items.AddRange(new object[]
        {
            "All jobs", "Needs review", "Rejected", "Unknown outcome", "Fiscalised", "Printing problems"
        });
        _filter.SelectedIndex = 0;
        UiTheme.Input(_filter);
        _filter.SelectedIndexChanged += (_, _) => RefreshList();

        _search = new TextBox
        {
            Width = 240,
            PlaceholderText = "Invoice, fiscal code, reason…"
        };
        UiTheme.Input(_search);
        _search.TextChanged += (_, _) => RefreshList();
        UiTheme.Tip(_search, "Search invoice, fiscal code, verification code, reason, source file or job hash.");

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        UiTheme.Secondary(refresh);
        refresh.Click += (_, _) => RefreshList();

        _count = UiTheme.Subheading("0 jobs");
        _count.Margin = new Padding(14, 9, 3, 3);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 8, 20, 6),
            WrapContents = false
        };
        toolbar.Controls.Add(UiTheme.SectionLabel("Show"));
        toolbar.Controls.Add(_filter);
        toolbar.Controls.Add(UiTheme.SectionLabel("Search"));
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_count);

        _list = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false
        };
        UiTheme.StyleGrid(_list);
        _list.RowTemplate.Height = 44;
        _list.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex >= _list.Columns.Count)
            {
                return;
            }

            if (_list.Rows[e.RowIndex].Tag is not LedgerEntry entry)
            {
                return;
            }

            e.ToolTipText = e.ColumnIndex switch
            {
                ColDetail => FullDetail(entry),
                ColDelete => CanDeleteLocal(entry, out var block)
                    ? "Delete this local hold and its retained receipt from this till. Nothing was sent to FiscalStack."
                    : block,
                _ => _list.Rows[e.RowIndex].Cells[e.ColumnIndex].FormattedValue?.ToString() ?? ""
            };
        };
        BuildColumns();
        _list.Columns[ColDetail].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _list.SelectionChanged += (_, _) => ShowSelection();
        _list.CellDoubleClick += (_, _) => ShowSelectedDetails(preview: true);
        _list.CellClick += (_, e) => OnCellClick(e);
        _list.CellPainting += (_, e) => PaintCells(e);

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Canvas, Padding = new Padding(14, 8, 14, 4) };
        gridHost.Controls.Add(_list);

        var detailCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(14, 4, 14, 4) };

        // A table keeps the heading, the review explanation and the job record in fixed
        // bands. Docking them directly in the card let the record collapse to a single
        // line when a long review note was visible.
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _reviewRow = new RowStyle(SizeType.Absolute, 0);
        detailLayout.RowStyles.Add(_reviewRow);
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var detailTitle = UiTheme.Heading("Selected job");
        detailTitle.Dock = DockStyle.Fill;
        detailTitle.AutoSize = false;
        detailTitle.Margin = new Padding(2, 0, 2, 0);

        _details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.Surface,
            Font = UiTheme.Mono(8.5f),
            ForeColor = UiTheme.Text,
            Padding = new Padding(4, 6, 4, 4),
            WordWrap = true
        };
        UiTheme.Input(_details);
        _details.BorderStyle = BorderStyle.None;
        _details.Font = UiTheme.Mono(8.5f);

        _reviewNote = new CardPanel
        {
            Dock = DockStyle.Fill,
            CardBackColor = UiTheme.WarningBg,
            CardBorderColor = UiTheme.Warning,
            CornerRadius = 10,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(2, 0, 2, 2),
            Visible = false
        };
        var reviewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        reviewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _reviewTitle = UiTheme.SectionLabel("NEEDS REVIEW — READ THE CAUSE BEFORE ACTING");
        _reviewTitle.Dock = DockStyle.Fill;
        _reviewTitle.ForeColor = UiTheme.Warning;
        _reviewTitle.Margin = Padding.Empty;
        _reviewText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.WarningBg,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Ui(8.5f),
            WordWrap = true,
            Margin = Padding.Empty,
            Padding = new Padding(2)
        };
        reviewLayout.Controls.Add(_reviewTitle, 0, 0);
        reviewLayout.Controls.Add(_reviewText, 0, 1);
        _reviewNote.Controls.Add(reviewLayout);

        detailLayout.Controls.Add(detailTitle, 0, 0);
        detailLayout.Controls.Add(_reviewNote, 0, 1);
        detailLayout.Controls.Add(_details, 0, 2);
        detailCard.Controls.Add(detailLayout);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 10, 20, 8),
            WrapContents = false
        };
        Button Action(string text, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(122, 36),
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

        var close = Action("Close");
        close.DialogResult = DialogResult.Cancel;
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _closeButton = close;
        _reprint = Action("Print fiscal copy", primary: true);
        _reprint.Enabled = false;
        _reprint.Click += (_, _) => ReprintSelected();
        _savePdf = Action("Save PDF copy");
        _savePdf.Enabled = false;
        _savePdf.Click += (_, _) => SavePdfSelected();
        _previewPdf = Action("Preview PDF");
        _previewPdf.Enabled = false;
        _previewPdf.Click += (_, _) => ShowSelectedDetails(preview: true);
        _openJob = Action("Open source");
        _openJob.Enabled = false;
        _openJob.Click += (_, _) => OpenSourceJob();
        _deleteLocal = Action("Delete local hold");
        _deleteLocal.Enabled = false;
        _deleteLocal.Click += (_, _) => DeleteSelected();
        var detailsButton = Action("Full detail");

        detailsButton.Click += (_, _) => ShowSelectedDetails();

        actions.Controls.Add(close);
        actions.Controls.Add(_deleteLocal);
        actions.Controls.Add(_reprint);
        actions.Controls.Add(_savePdf);
        actions.Controls.Add(_previewPdf);
        actions.Controls.Add(_openJob);
        actions.Controls.Add(detailsButton);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(gridHost, 0, 2);
        root.Controls.Add(detailCard, 0, 3);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);

        CancelButton = close;
        RefreshList();
    }

    private void BuildColumns()
    {
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Updated", DataPropertyName = "Updated", Width = 112
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Invoice", DataPropertyName = "Invoice", Width = 140
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Status", DataPropertyName = "Status", Width = 108
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Total", DataPropertyName = "Total", Width = 96,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Print", DataPropertyName = "Print", Width = 88
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Detail", DataPropertyName = "Detail", Width = 220
        });

        AddActionColumn("Open", UiTheme.Accent, UiTheme.AccentLight);
        AddActionColumn("PDF", UiTheme.Success, UiTheme.SuccessBg);
        AddActionColumn("Print", UiTheme.Warning, UiTheme.WarningBg);
        AddActionColumn("Delete", UiTheme.Danger, UiTheme.DangerBg);
        ResizeColumns();
    }

    private void AddActionColumn(string text, Color fore, Color back)
    {
        var column = new DataGridViewButtonColumn
        {
            HeaderText = "",
            Text = text,
            Width = 76,
            MinimumWidth = 76,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        UiTheme.ButtonColumn(column, fore, back);
        _list.Columns.Add(column);
    }

    private void RefreshList()
    {
        var selectedHash = SelectedEntry()?.Hash;
        var entries = _ledger.All();
        var filter = _filter.SelectedIndex;
        var query = _search.Text.Trim();

        _list.SuspendLayout();
        _list.Rows.Clear();

        var shown = 0;
        foreach (var entry in entries)
        {
            if (!Matches(entry, filter, query))
            {
                continue;
            }

            var index = _list.Rows.Add(
                entry.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                entry.InvoiceNumber ?? "-",
                entry.Status,
                string.IsNullOrWhiteSpace(entry.Currency)
                    ? entry.Total.ToString("0.00")
                    : $"{entry.Currency} {entry.Total:0.00}",
                PrintText(entry),
                ShortDetail(entry),
                "Open",
                "PDF",
                "Print",
                "Delete");
            _list.Rows[index].Tag = entry;
            shown++;
        }

        _list.ResumeLayout();
        _count.Text = $"{shown} of {entries.Count} job(s)";
        ResizeColumns();

        if (selectedHash is not null)
        {
            foreach (DataGridViewRow row in _list.Rows)
            {
                if (row.Tag is LedgerEntry entry &&
                    string.Equals(entry.Hash, selectedHash, StringComparison.Ordinal))
                {
                    row.Selected = true;
                    _list.CurrentCell = row.Cells[ColInvoice];
                    break;
                }
            }
        }

        ShowSelection();
    }

    private static bool Matches(LedgerEntry entry, int filter, string query)
    {
        var statusMatch = filter switch
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
        if (!statusMatch || query.Length == 0)
        {
            return statusMatch;
        }

        return new[]
        {
            entry.InvoiceNumber, entry.FiscalCode, entry.VerificationCode, entry.Error,
            entry.ReviewStage, entry.ReviewHint, entry.PrintError, entry.SourceFile, entry.Hash
        }.Any(value => value is not null &&
                       value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void PaintCells(DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is not { } graphics)
        {
            return;
        }

        if (e.ColumnIndex == ColStatus && e.Value is LedgerStatus status)
        {
            e.PaintBackground(e.CellBounds, true);
            var pill = UiTheme.Status(status);
            UiTheme.PaintPill(graphics, e.CellBounds, pill.Text, pill.Fore, pill.Back);
            e.Handled = true;
            return;
        }

        if (e.ColumnIndex is ColOpen or ColPdf or ColReprint or ColDelete)
        {
            e.PaintBackground(e.CellBounds, true);
            var (fore, back) = e.ColumnIndex switch
            {
                ColOpen => (UiTheme.Accent, UiTheme.AccentLight),
                ColPdf => (UiTheme.Success, UiTheme.SuccessBg),
                ColReprint => (UiTheme.Warning, UiTheme.WarningBg),
                _ => (UiTheme.Danger, UiTheme.DangerBg)
            };

            var label = e.FormattedValue?.ToString() ?? "";
            if (e.ColumnIndex == ColDelete &&
                _list.Rows[e.RowIndex].Tag is LedgerEntry entry &&
                !CanDeleteLocal(entry, out _))
            {
                label = "";
                fore = UiTheme.Muted;
                back = UiTheme.NeutralBg;
            }

            UiTheme.PaintPill(graphics, e.CellBounds, label, fore, back);
            e.Handled = true;
        }
    }

    private void OnCellClick(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex is not (ColOpen or ColPdf or ColReprint or ColDelete))
        {
            return;
        }

        _list.Rows[e.RowIndex].Selected = true;
        if (e.ColumnIndex == ColOpen)
        {
            OpenSourceJob();
        }
        else if (e.ColumnIndex == ColPdf)
        {
            SavePdfSelected();
        }
        else if (e.ColumnIndex == ColDelete)
        {
            DeleteSelected();
        }
        else
        {
            ReprintSelected();
        }
    }

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
            var stage = string.IsNullOrWhiteSpace(entry.ReviewStage) ? "" : entry.ReviewStage + ": ";
            return stage + entry.Error;
        }

        if (!string.IsNullOrWhiteSpace(entry.PrintError))
        {
            return entry.PrintError!;
        }

        return entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline
            ? "Fiscal proof recorded"
            : "";
    }

    /// <summary>Full reason and next step, used by cell tooltips so nothing is lost to a narrow grid.</summary>
    private static string FullDetail(LedgerEntry entry)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            lines.Add(entry.Error!);
        }

        if (!string.IsNullOrWhiteSpace(entry.ReviewHint))
        {
            lines.Add("Next step: " + entry.ReviewHint);
        }

        if (entry.ReviewWarnings.Length > 0)
        {
            lines.AddRange(entry.ReviewWarnings.Select(warning => "Warning: " + warning));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private LedgerEntry? SelectedEntry() =>
        _list.CurrentRow is { } row && row.Tag is LedgerEntry entry ? entry : null;

    private void ShowSelection()
    {
        var entry = SelectedEntry();
        var printable = entry is not null &&
                        entry.Status is LedgerStatus.Fiscalised or LedgerStatus.FiscalisedOffline;
        _reprint.Enabled = printable && entry is not null && _snapshots.ExistsDurable(entry.Hash);
        _savePdf.Enabled = entry is not null && FiscalDocumentActions.CanSavePdf(entry, _snapshots);
        _previewPdf.Enabled = _savePdf.Enabled;
        _openJob.Enabled = entry is not null && FiscalDocumentActions.CanOpen(entry, _snapshots, _jobs);
        _deleteLocal.Enabled = entry is not null && CanDeleteLocal(entry, out _);
        _details.Text = entry is null ? "" : Describe(entry);
        ShowReviewNote(entry);
    }

    private void ShowReviewNote(LedgerEntry? entry)
    {
        if (entry?.Status != LedgerStatus.NeedsReview)
        {
            _reviewRow.Height = 0;
            _reviewNote.Visible = false;
            return;
        }

        var deletable = CanDeleteLocal(entry, out _);
        _reviewTitle.Text = UiTheme.SafeText(deletable
            ? "NEEDS REVIEW — NO REQUEST WAS SENT; THIS LOCAL HOLD CAN BE DELETED"
            : "NEEDS REVIEW — KEEP THE RECORD FOR RECONCILIATION");
        _reviewText.Text = string.Join(Environment.NewLine,
            $"Stage: {entry.ReviewStageText}",
            $"Cause: {entry.ReviewReasonText}",
            $"Next step: {entry.ReviewNextStepText}",
            $"Warnings: {entry.ReviewWarningsText}");
        _reviewText.SelectionStart = 0;
        _reviewText.ScrollToCaret();
        _reviewNote.Visible = true;
        _reviewRow.Height = 104;
    }

    private void ShowSelectedDetails(bool preview = false)
    {
        if (SelectedEntry() is { } entry)
        {
            using var detail = new JobDetailForm(entry, _jobs, _snapshots, _log, preview);
            detail.ShowDialog(UiTheme.DialogOwner(this));
            RefreshList();
        }
    }

    private string Describe(LedgerEntry entry)
    {
        var copy = _snapshots.ExistsDurable(entry.Hash) ? "archived" : "no";
        var held = entry.Status == LedgerStatus.NeedsReview;
        return string.Join(Environment.NewLine, new[]
        {
            $"Hash............ {entry.Hash}",
            $"Status.......... {UiTheme.Status(entry.Status).Text}",
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
            $"Review stage.... {(held ? entry.ReviewStageText : OrDash(entry.ReviewStage))}",
            $"Reason.......... {(held ? entry.ReviewReasonText : OrDash(entry.Error))}",
            $"Next step....... {(held ? entry.ReviewNextStepText : OrDash(entry.ReviewHint))}",
            $"Warnings........ {(held ? entry.ReviewWarningsText : (entry.ReviewWarnings.Length == 0 ? "-" : string.Join(" | ", entry.ReviewWarnings)))}",
            $"Source job...... {OrDash(entry.SourceFile)}",
            $"Fiscal copy..... {copy}"
        });
    }

    /// <summary>Used by the dashboard's refresh control to re-read this page too.</summary>
    public void RefreshView() => RefreshList();

    private bool CanDeleteLocal(LedgerEntry entry, out string reason)
    {
        // The request body is written just before the gateway call. If one survived, this job
        // may have been sent even when the ledger predates the RequestCreated flag.
        var requestBodyRetained = _jobs.LoadRequest(entry.SourceFile) is not null;
        reason = entry.LocalDeletionBlockReason(requestBodyRetained) ?? "";
        return reason.Length == 0;
    }

    private void DeleteSelected()
    {
        if (SelectedEntry() is not { } entry)
        {
            return;
        }

        if (!CanDeleteLocal(entry, out var block))
        {
            MessageBox.Show(UiTheme.DialogOwner(this), block, "This job cannot be deleted",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(UiTheme.DialogOwner(this),
            $"Delete the local hold for {entry.InvoiceNumber ?? "this invoice"}?" +
            Environment.NewLine + Environment.NewLine +
            "No fiscal request was created for this job. Its ledger entry, retained receipt text " +
            "and stored original will be removed from this till. This cannot be undone.",
            "Delete local hold",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var requestBodyRetained = _jobs.LoadRequest(entry.SourceFile) is not null;
            if (!_ledger.DeleteLocalHold(entry.Hash, requestBodyRetained, out var error))
            {
                MessageBox.Show(UiTheme.DialogOwner(this), error, "This job cannot be deleted",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _jobs.DeleteRetained(entry.Hash, entry.SourceFile);
            _snapshots.DeleteLocalSnapshot(entry.Hash);
            RefreshList();
        }
        catch (Exception ex)
        {
            _log.Error("Deleting a local hold failed", ex);
            MessageBox.Show(UiTheme.DialogOwner(this), ex.Message, "Could not delete the local hold",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshList();
        }
    }

    private void SavePdfSelected()
    {
        var entry = SelectedEntry();
        if (entry is null)
        {
            return;
        }

        if (!FiscalDocumentActions.TrySavePdf(entry, _snapshots, _log, out var path, out var error))
        {
            MessageBox.Show(UiTheme.DialogOwner(this), error, "Could not save the PDF copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(UiTheme.DialogOwner(this),
            $"Saved the fiscal copy to:{Environment.NewLine}{path}",
            "PDF copy saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ReprintSelected()
    {
        var entry = SelectedEntry();
        if (entry is null)
        {
            return;
        }

        var answer = MessageBox.Show(UiTheme.DialogOwner(this),
            $"Print the fiscal copy for {entry.InvoiceNumber ?? "this invoice"}?" +
            Environment.NewLine + Environment.NewLine +
            "The copy is marked REPRINT. The receipt is not fiscalised again.",
            "Print fiscal copy",
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
                MessageBox.Show(UiTheme.DialogOwner(this), "The fiscal copy was sent to the printer.", "Print fiscal copy",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(UiTheme.DialogOwner(this), error, "Could not print the copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Printing from the jobs window failed", ex);
            MessageBox.Show(UiTheme.DialogOwner(this), ex.Message, "Could not print the copy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        RefreshList();
    }

    private void OpenSourceJob()
    {
        var entry = SelectedEntry();
        if (entry is null)
        {
            return;
        }

        var path = FiscalDocumentActions.OpenablePath(entry, _snapshots, _jobs);
        if (path is not null)
        {
            ShellOpen.Path(path);
        }
    }

    private void ResizeColumns()
    {
        if (_list.Columns.Count <= ColDelete)
        {
            return;
        }

        var fixedWidth = 0;
        for (var i = 0; i < _list.Columns.Count; i++)
        {
            if (i != ColDetail)
            {
                fixedWidth += _list.Columns[i].Width;
            }
        }

        _list.Columns[ColDetail].Width = Math.Max(180, _list.ClientSize.Width - fixedWidth - 2);
    }

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;
}
