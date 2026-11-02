using System.Text;
using System.Text.Json;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

/// <summary>
/// Turns one of the receipts this till already caught into a parse profile, so a POS nobody has
/// written rules for can be onboarded from the paper rather than by hand.
///
/// The window is deliberately an evidence view, not a wizard: every rule it offers carries the line
/// it was read from and the reason it is needed, the same receipt is parsed twice side by side so the
/// operator sees what changes before anything is saved, and anything a receipt cannot prove — which
/// VAT bucket a code means, whether this document is a return — is reported as a note instead of
/// written into the profile.
/// </summary>
public sealed class ProfileCaptureForm : Form
{
    private readonly JobLedger _ledger;
    private readonly JobStore _jobs;
    private readonly InterceptorConfig _config;
    private readonly ParseProfileStore _profiles;
    private readonly ILog _log;

    private readonly ComboBox _job;
    private readonly TextBox _receipt;
    private readonly Label _currentStatus;
    private readonly Label _suggestedStatus;
    private readonly ListView _currentFields;
    private readonly ListView _suggestedFields;
    private readonly ListView _suggestions;
    private readonly TextBox _notes;
    private readonly TextBox _name;
    private readonly TextBox _title;
    private readonly Button _save;
    private readonly Button _apply;

    private ProfileCapture? _capture;
    private string? _suggestedName;
    private List<LedgerEntry> _entries = new();

    public ProfileCaptureForm(
        JobLedger ledger,
        JobStore jobs,
        InterceptorConfig config,
        ParseProfileStore profiles,
        ILog log)
    {
        _ledger = ledger;
        _jobs = jobs;
        _config = config;
        _profiles = profiles;
        _log = log;

        Text = "Capture a receipt layout profile";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(900, 560);

        _job = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 420
        };
        _job.SelectedIndexChanged += (_, _) => LoadSelectedJob();

        var fromFile = new Button { Text = "From file…", AutoSize = true };
        fromFile.Click += (_, _) => LoadFromFile();

        var analyse = new Button { Text = "Analyse", AutoSize = true };
        analyse.Click += (_, _) => Analyse();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 4)
        };
        toolbar.Controls.Add(new Label { Text = "Receipt:", AutoSize = true, Margin = new Padding(3, 8, 6, 3) });
        toolbar.Controls.Add(_job);
        toolbar.Controls.Add(fromFile);
        toolbar.Controls.Add(analyse);

        _receipt = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            HideSelection = false,
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle
        };

        var receiptText = new GroupBox
        {
            Text = "The job exactly as this till received it (editable — paste a receipt from another till)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        receiptText.Controls.Add(_receipt);

        _currentFields = FieldList();
        _suggestedFields = FieldList();
        _currentStatus = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 6) };
        _suggestedStatus = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 6) };

        _suggestions = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false
        };
        _suggestions.Columns.Add("Profile rule", 210);
        _suggestions.Columns.Add("Values it writes", 300);
        _suggestions.Columns.Add("Line", 46, HorizontalAlignment.Right);
        _suggestions.Columns.Add("Why", 420);

        _notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(252, 250, 240),
            BorderStyle = BorderStyle.FixedSingle
        };

        var results = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0)
        };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 48f));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 28f));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 24f));
        results.Controls.Add(ReadingGroup("With the rules this till uses now", _currentStatus, _currentFields), 0, 0);
        results.Controls.Add(ReadingGroup("With the profile being suggested", _suggestedStatus, _suggestedFields), 1, 0);

        var suggestionGroup = Group("Rules the profile adds", _suggestions);
        var noteGroup = Group("What a profile cannot decide", _notes);
        results.Controls.Add(suggestionGroup, 0, 1);
        results.SetColumnSpan(suggestionGroup, 2);
        results.Controls.Add(noteGroup, 0, 2);
        results.SetColumnSpan(noteGroup, 2);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 420,
            Panel1MinSize = 260,
            Panel2MinSize = 380
        };
        split.Panel1.Controls.Add(receiptText);
        split.Panel2.Controls.Add(results);

        _name = new TextBox { Width = 170 };
        _title = new TextBox { Width = 220 };

        _save = new Button { Text = "Save profile", AutoSize = true, Enabled = false };
        _save.Click += (_, _) => SaveProfile(apply: false);
        _apply = new Button { Text = "Save and use now", AutoSize = true, Enabled = false };
        _apply.Click += (_, _) => SaveProfile(apply: true);
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8)
        };
        footer.Controls.Add(new Label { Text = "Profile id:", AutoSize = true, Margin = new Padding(3, 8, 4, 3) });
        footer.Controls.Add(_name);
        footer.Controls.Add(new Label { Text = "Shown as:", AutoSize = true, Margin = new Padding(10, 8, 4, 3) });
        footer.Controls.Add(_title);
        footer.Controls.Add(_save);
        footer.Controls.Add(_apply);
        footer.Controls.Add(close);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(toolbar);

        CancelButton = close;
        AcceptButton = analyse;

        ReloadJobs();
    }

    private static ListView FieldList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false
        };
        list.Columns.Add("Field", 150);
        list.Columns.Add("Read", 170);
        list.Columns.Add("Line", 44, HorizontalAlignment.Right);
        return list;
    }

    private static GroupBox ReadingGroup(string caption, Label status, ListView fields)
    {
        var group = new GroupBox
        {
            Text = caption,
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        host.Controls.Add(status, 0, 0);
        host.Controls.Add(fields, 0, 1);
        group.Controls.Add(host);
        return group;
    }

    private static GroupBox Group(string caption, Control content)
    {
        var group = new GroupBox
        {
            Text = caption,
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        group.Controls.Add(content);
        return group;
    }

    /// <summary>The jobs this till caught, newest first, that still have their receipt text on disk.</summary>
    private void ReloadJobs()
    {
        _entries = _ledger.All()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceFile) && File.Exists(entry.SourceFile))
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(200)
            .ToList();

        _job.Items.Clear();
        foreach (var entry in _entries)
        {
            _job.Items.Add(Describe(entry));
        }

        if (_job.Items.Count == 0)
        {
            _job.Items.Add("No retained job — turn storage.retainJobs on, or use “From file…”");
        }

        if (_job.Items.Count > 0)
        {
            _job.SelectedIndex = 0;
        }
    }

    private static string Describe(LedgerEntry entry) =>
        $"{entry.UpdatedAt:yyyy-MM-dd HH:mm}  {(string.IsNullOrWhiteSpace(entry.InvoiceNumber) ? "(no number read)" : entry.InvoiceNumber)}  " +
        $"{entry.Status}  {entry.Total:0.00} {entry.Currency}  [{(entry.Hash.Length >= 8 ? entry.Hash[..8] : entry.Hash)}]";

    private void LoadSelectedJob()
    {
        var index = _job.SelectedIndex;
        if (index < 0 || index >= _entries.Count)
        {
            _receipt.Text = "";
            Analyse();
            return;
        }

        var text = _jobs.LoadText(_entries[index].SourceFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            var described = Describe(_entries[index]);
            _job.Items[index] = described.EndsWith("text not retained", StringComparison.Ordinal)
                ? described
                : described + "  — text not retained";
            _receipt.Text = "";
            Analyse();
            return;
        }

        _receipt.Text = text;
        Analyse();
    }

    private void LoadFromFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a receipt the POS printed",
            Filter = "Receipt text (*.txt;*.prn;*.log)|*.txt;*.prn;*.log|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _receipt.Text = File.ReadAllText(dialog.FileName);
            Analyse();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The file could not be read: {ex.Message}", "Open failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Analyse()
    {
        var text = _receipt.Text ?? "";
        if (text.Trim().Length == 0)
        {
            _capture = null;
            _currentStatus.Text = "Nothing to read yet.";
            _suggestedStatus.Text = "";
            _currentFields.Items.Clear();
            _suggestedFields.Items.Clear();
            _suggestions.Items.Clear();
            _notes.Text = "";
            _save.Enabled = false;
            _apply.Enabled = false;
            return;
        }

        ProfileCapture capture;
        try
        {
            capture = ProfileCapturer.Capture(text, _config.Parse);
        }
        catch (Exception ex)
        {
            _capture = null;
            _currentStatus.Text = "The capture failed: " + ex.Message;
            _save.Enabled = false;
            _apply.Enabled = false;
            _log.Error("Parse profile capture failed", ex);
            return;
        }

        _capture = capture;
        Show(capture);
    }

    private void Show(ProfileCapture capture)
    {
        _currentStatus.Text = Status(capture.Current);
        _suggestedStatus.Text = Status(capture.Suggested);
        Fill(_currentFields, capture.Current);
        Fill(_suggestedFields, capture.Suggested);

        _suggestions.Items.Clear();
        foreach (var suggestion in capture.Suggestions)
        {
            var item = new ListViewItem(suggestion.Property);
            item.SubItems.Add(string.Join(", ", suggestion.Values));
            item.SubItems.Add(suggestion.Line == 0 ? "—" : suggestion.Line.ToString());
            item.SubItems.Add(suggestion.Because);
            item.UseItemStyleForSubItems = false;
            item.BackColor = capture.Current.IsSuccess ? Color.FromArgb(240, 248, 255) : Color.FromArgb(235, 250, 235);
            _suggestions.Items.Add(item);
        }

        var notes = new StringBuilder();
        if (!capture.HasSuggestions)
        {
            notes.AppendLine("This receipt adds no rule: the layout is already described by the profile in use" +
                            (string.IsNullOrWhiteSpace(_config.Parse.Profile) ? " (the built-in rules)." : $" '{_config.Parse.Profile}'."));
        }

        foreach (var note in capture.Notes)
        {
            notes.AppendLine("• " + note);
        }

        _notes.Text = notes.ToString();

        // Only re-suggested while the operator has not typed over it: switching from one POS's
        // receipt to another's should update the id, a hand-written one should survive.
        if (_name.Text.Length == 0 || _name.Text == _suggestedName)
        {
            _suggestedName = SuggestName(capture);
            _name.Text = _suggestedName;
            _title.Text = SuggestTitle(capture);
        }

        var enabled = capture.HasSuggestions;
        _save.Enabled = enabled;
        _apply.Enabled = enabled;
    }

    private static string Status(CaptureReading reading) =>
        reading.Result.Status.ToString() + " · " + reading.Items + " item(s)" +
        (reading.Result.Reason is { Length: > 0 } reason ? " · " + reason : "");

    private static void Fill(ListView list, CaptureReading reading)
    {
        list.Items.Clear();
        foreach (var field in reading.Fields)
        {
            var item = new ListViewItem(field.Name);
            item.SubItems.Add(field.Found ? field.Value : "— not read —");
            item.SubItems.Add(field.Line == 0 ? "—" : field.Line.ToString());
            item.UseItemStyleForSubItems = false;
            if (!field.Found)
            {
                item.ForeColor = Color.Firebrick;
            }

            list.Items.Add(item);
        }
    }

    private static string SuggestName(ProfileCapture capture)
    {
        var heading = capture.Lines.FirstOrDefault(line => line.Trim().Length > 0) ?? "pos";
        var builder = new StringBuilder();
        foreach (var character in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-' && character is ' ' or '-' or '_' or '.')
            {
                builder.Append('-');
            }

            if (builder.Length >= 32)
            {
                break;
            }
        }

        var name = builder.ToString().Trim('-');
        return name.Length == 0 ? "unknown-pos" : name;
    }

    private static string SuggestTitle(ProfileCapture capture)
    {
        var heading = capture.Lines.FirstOrDefault(line => line.Trim().Length > 0)?.Trim();
        return string.IsNullOrWhiteSpace(heading) ? "Captured POS layout" : heading;
    }

    private void SaveProfile(bool apply)
    {
        if (_capture is not { HasSuggestions: true } capture)
        {
            MessageBox.Show(this, "There is nothing to save: this receipt suggests no rule the till does not already have.",
                "Nothing to save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = _name.Text.Trim();
        if (!InterceptorConfig.IsProfileId(name))
        {
            MessageBox.Show(this,
                "A profile id is 1-40 letters, digits or dashes, starting with a letter or digit — for example \"kopko-till\".",
                "Check the profile id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }

        var profile = new ParseProfile
        {
            Name = name,
            Title = _title.Text.Trim(),
            Description = $"Captured from a live job on {DateTime.Now:yyyy-MM-dd}.",
            Rules = capture.Rules
        };

        try
        {
            var path = _profiles.Save(profile);
            _log.Info($"Captured parse profile '{name}' from a live job to {path}.");

            if (apply)
            {
                _profiles.Apply(name, _config);
                _config.Save();
                _log.Info($"Captured and applied parse profile '{name}'; the rules were written to the configuration file.");
                MessageBox.Show(this,
                    $"Profile '{name}' was saved to:\r\n{path}\r\n\r\nIt is now this till's layout, and its rules are " +
                    $"saved in {_config.ConfigPath}.\n\nRestart PrintInterceptor (or the service) to parse with them; " +
                    "the receipts it has already read are unaffected.",
                    "Profile saved and applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            MessageBox.Show(this,
                $"Profile '{name}' was saved to:\r\n{path}\r\n\r\nChoose it in Settings to start using it.",
                "Profile saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, $"The profile could not be saved: {ex.Message}", "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
