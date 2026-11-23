using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.Pipeline;

namespace PrintInterceptor;

/// <summary>
/// The receipt rules as controls, not a JSON file: labels, regexes, tax buckets, correction
/// words and guard rails are all editable here. Edits go to a draft, so a broken regex can be
/// reverted, and are only committed after the same validation an imported profile has to pass.
/// Saving rebuilds the running parser, unlike a hand-edited config file which needed a restart.
/// </summary>
public sealed class ParseRulesPage : UserControl
{
    private static readonly string[] Categories =
    {
        "Labels & markers", "Patterns", "Tax buckets", "Corrections", "Limits", "Profiles", "Test a receipt"
    };

    private readonly InterceptorConfig _config;
    private readonly PrintJobPipeline _pipeline;
    private readonly ILog _log;
    private readonly ParseProfileStore _profiles;
    private readonly ParseConfig _draft;
    private readonly ListBox _nav;
    private readonly Panel _host;
    private readonly Label _status;
    private readonly TextBox _sample;
    private readonly TextBox _result;

    public ParseRulesPage(InterceptorConfig config, PrintJobPipeline pipeline, ILog log)
    {
        _config = config;
        _pipeline = pipeline;
        _log = log;
        _profiles = ParseProfileStore.ForCurrentInstall();
        _draft = Clone(config.Parse);

        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Padding = new Padding(10);

        _nav = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 180,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            IntegralHeight = false
        };
        _nav.Items.AddRange(Categories);

        _host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 0, 8, 8) };
        _sample = new TextBox();
        _result = new TextBox();

        _status = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(10, 14, 6, 10)
        };

        var validate = new Button { Text = "Validate", AutoSize = true, MinimumSize = new Size(94, 32) };
        validate.Click += (_, _) => ValidateDraft();
        var revert = new Button { Text = "Revert", AutoSize = true, MinimumSize = new Size(94, 32) };
        revert.Click += (_, _) => Revert();
        var save = new Button { Text = "Save and apply", AutoSize = true, MinimumSize = new Size(132, 32) };
        save.Click += (_, _) => Save();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(8)
        };
        actions.Controls.Add(save);
        actions.Controls.Add(revert);
        actions.Controls.Add(validate);
        actions.Controls.Add(_status);

        _nav.SelectedIndexChanged += (_, _) => ShowCategory();
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(actions);

        _nav.SelectedIndex = 0;
    }

    private void ShowCategory()
    {
        if (_nav.SelectedItem is not string category)
        {
            return;
        }

        _host.Controls.Clear();
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4, 10, 4, 4) };
        var heading = new Label
        {
            Text = category,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(2, 0, 2, 12)
        };
        page.Controls.Add(Body(category));
        page.Controls.Add(heading);
        _host.Controls.Add(page);
    }

    private Control Body(string category) => category switch
    {
        "Labels & markers" => LabelsPage(),
        "Patterns" => PatternsPage(),
        "Tax buckets" => TaxPage(),
        "Corrections" => CorrectionsPage(),
        "Limits" => LimitsPage(),
        "Profiles" => ProfilesPage(),
        "Test a receipt" => TestPage(),
        _ => new Panel()
    };

    private Control LabelsPage()
    {
        var grid = NewGrid();
        AddText(grid, "Item table starts", _draft.ProductStartLine, v => _draft.ProductStartLine = v);
        AddText(grid, "Item table ends", _draft.ProductEndLine, v => _draft.ProductEndLine = v);
        AddText(grid, "Invoice number label", _draft.InvoiceNumber, v => _draft.InvoiceNumber = v);
        AddText(grid, "Buyer name label", _draft.CustomerName, v => _draft.CustomerName = v);
        AddText(grid, "Buyer address label", _draft.CustomerAddress, v => _draft.CustomerAddress = v);
        AddText(grid, "Buyer email label", _draft.CustomerEmail, v => _draft.CustomerEmail = v);
        AddText(grid, "Buyer TIN label", _draft.CustomerTIN, v => _draft.CustomerTIN = v);
        AddText(grid, "Buyer VAT label", _draft.CustomerVATNumber, v => _draft.CustomerVATNumber = v);
        AddText(grid, "Buyer phone label", _draft.CustomerTelephoneNumber, v => _draft.CustomerTelephoneNumber = v);
        AddText(grid, "Currency", _draft.Currency, v => _draft.Currency = v);
        AddText(grid, "Payment method", _draft.PaymentMethod, v => _draft.PaymentMethod = v);
        AddList(grid, "Date labels", _draft.DateLabels, v => _draft.DateLabels = v);
        AddList(grid, "Accepted date formats", _draft.DateFormats, v => _draft.DateFormats = v);
        AddList(grid, "VAT number labels", _draft.VatNumberLabels, v => _draft.VatNumberLabels = v);
        AddList(grid, "TIN labels", _draft.TinLabels, v => _draft.TinLabels = v);
        AddList(grid, "Phone labels", _draft.PhoneLabels, v => _draft.PhoneLabels = v);
        AddList(grid, "Address labels", _draft.AddressLabels, v => _draft.AddressLabels = v);
        AddList(grid, "Walk-in buyer lines", _draft.BuyerNameLiterals, v => _draft.BuyerNameLiterals = v);
        AddList(grid, "Invoice number keywords", _draft.InvoiceNumberKeywords, v => _draft.InvoiceNumberKeywords = v);
        AddList(grid, "Buyer name keywords", _draft.BuyerNameKeywords, v => _draft.BuyerNameKeywords = v);
        AddList(grid, "Non-item keywords", _draft.NonItemKeywords, v => _draft.NonItemKeywords = v);
        AddList(grid, "Table header must contain", _draft.ItemTableHeaderRequires, v => _draft.ItemTableHeaderRequires = v);
        AddList(grid, "Table header any of", _draft.ItemTableHeaderAnyOf, v => _draft.ItemTableHeaderAnyOf = v);
        AddList(grid, "Table end markers", _draft.ItemTableEndMarkers, v => _draft.ItemTableEndMarkers = v);
        return Scrolled(grid);
    }

    private Control PatternsPage()
    {
        var grid = NewGrid();
        AddText(grid, "Invoice number", _draft.Patterns.InvoiceNumber, v => _draft.Patterns.InvoiceNumber = v, multiline: true);
        AddText(grid, "Item row", _draft.Patterns.InvoiceRow, v => _draft.Patterns.InvoiceRow = v, multiline: true);
        AddText(grid, "Two-line quantity", _draft.Patterns.TwoLineQuantity, v => _draft.Patterns.TwoLineQuantity = v, multiline: true);
        AddText(grid, "Two-line name + amount", _draft.Patterns.TwoLineNameAmount, v => _draft.Patterns.TwoLineNameAmount = v, multiline: true);
        AddText(grid, "Currency before amount", _draft.Patterns.CurrencyBeforeAmount, v => _draft.Patterns.CurrencyBeforeAmount = v, multiline: true);
        AddText(grid, "Buyer field label", _draft.Patterns.BuyerLabel, v => _draft.Patterns.BuyerLabel = v, multiline: true);
        AddText(grid, "Amount on its own line", _draft.Patterns.AmountLine, v => _draft.Patterns.AmountLine = v, multiline: true);
        AddText(grid, "Buyer email", _draft.Patterns.Email, v => _draft.Patterns.Email = v, multiline: true);
        return Scrolled(grid);
    }

    private Control TaxPage()
    {
        var grid = NewGrid();
        AddBool(grid, "Printed prices include VAT", _draft.VatInclusive, v => _draft.VatInclusive = v);
        AddNumber(grid, "Local STANDARD rate (%)", (decimal)_draft.VatRate * 100m, 0m, 100m, 3, v => _draft.VatRate = (double)(v / 100m));

        var buckets = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items =
            {
                ParseConfig.TaxTypeStandard,
                ParseConfig.TaxTypeZeroRated,
                ParseConfig.TaxTypeExempt
            },
            Text = _draft.DefaultTaxType
        };
        buckets.SelectedValueChanged += (_, _) => _draft.DefaultTaxType = buckets.Text;
        AddRow(grid, "Default bucket", buckets);

        AddText(grid, "VAT-bearing marker", _draft.TaxSymbol, v => _draft.TaxSymbol = v);
        AddText(grid, "Exempt marker", _draft.NonTaxSymbol, v => _draft.NonTaxSymbol = v);

        var rules = new DataGridView
        {
            Dock = DockStyle.Fill,
            Height = 180,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White
        };
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bucket", HeaderText = "Tax bucket" });
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Markers", HeaderText = "Markers (one per line in the cell)" });
        rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Where", HeaderText = "Where (slot, annotation, section)" });
        foreach (var rule in _draft.TaxRules)
        {
            rules.Rows.Add(rule.TaxType, Join(rule.Markers), Join(rule.Where));
        }

        var loading = true;
        rules.CellValueChanged += (_, _) =>
        {
            if (loading)
            {
                return;
            }

            _draft.TaxRules = rules.Rows
                .Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(row => new TaxMarkerRule
                {
                    TaxType = CellText(row.Cells["Bucket"].Value),
                    Markers = Lines(CellText(row.Cells["Markers"].Value)),
                    Where = Lines(CellText(row.Cells["Where"].Value))
                })
                .Where(rule => rule.TaxType.Length > 0 || rule.Markers.Count > 0)
                .ToList();
        };
        loading = false;
        AddWideRow(grid, "Marker rules (highest precedence first)", rules, 210);

        var currencies = new DataGridView
        {
            Dock = DockStyle.Fill,
            Height = 140,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White
        };
        currencies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Keyword", HeaderText = "Printed keyword" });
        currencies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Currency code" });
        foreach (var currency in _draft.Currencies)
        {
            currencies.Rows.Add(currency.Keyword, currency.Name);
        }

        var loadingCurrencies = true;
        currencies.CellValueChanged += (_, _) =>
        {
            if (loadingCurrencies)
            {
                return;
            }

            _draft.Currencies = currencies.Rows
                .Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .Select(row => new CurrencyKeyword
                {
                    Keyword = CellText(row.Cells["Keyword"].Value),
                    Name = CellText(row.Cells["Name"].Value)
                })
                .Where(currency => currency.Keyword.Length > 0 || currency.Name.Length > 0)
                .ToList();
        };
        loadingCurrencies = false;
        AddWideRow(grid, "Currency keywords", currencies, 170);
        return Scrolled(grid);
    }

    private Control CorrectionsPage()
    {
        var grid = NewGrid();
        AddBool(grid, "A negative total means credit note", _draft.Corrections.NegativeTotalMeansCreditNote,
            v => _draft.Corrections.NegativeTotalMeansCreditNote = v);
        AddList(grid, "Credit note words", _draft.Corrections.CreditNoteKeywords,
            v => _draft.Corrections.CreditNoteKeywords = v);
        AddList(grid, "Debit note words", _draft.Corrections.DebitNoteKeywords,
            v => _draft.Corrections.DebitNoteKeywords = v);
        AddList(grid, "Credit note number prefixes", _draft.Corrections.CreditNoteNumberPrefixes,
            v => _draft.Corrections.CreditNoteNumberPrefixes = v);
        AddList(grid, "Original invoice labels", _draft.Corrections.OriginalInvoiceLabels,
            v => _draft.Corrections.OriginalInvoiceLabels = v);
        AddList(grid, "Reason labels", _draft.Corrections.ReasonLabels,
            v => _draft.Corrections.ReasonLabels = v);
        return Scrolled(grid);
    }

    private Control LimitsPage()
    {
        var grid = NewGrid();
        AddNumber(grid, "Line tolerance", _draft.Limits.LineTolerance, 0m, 100m, 3, v => _draft.Limits.LineTolerance = v);
        AddNumber(grid, "Relative tolerance", _draft.Limits.RelativeTolerance, 0m, 100m, 4, v => _draft.Limits.RelativeTolerance = v);
        AddNumber(grid, "Minimum confidence", (decimal)_draft.Limits.MinConfidence, 0m, 1m, 3, v => _draft.Limits.MinConfidence = (double)v);
        AddNumber(grid, "Minimum item tokens", _draft.Limits.MinItemTokens, 1m, 20m, 0, v => _draft.Limits.MinItemTokens = (int)v);
        AddNumber(grid, "Maximum invoice number length", _draft.Limits.MaxInvoiceNumberLength, 1m, 500m, 0, v => _draft.Limits.MaxInvoiceNumberLength = (int)v);
        AddNumber(grid, "Maximum buyer name length", _draft.Limits.MaxBuyerNameLength, 1m, 2000m, 0, v => _draft.Limits.MaxBuyerNameLength = (int)v);
        AddNumber(grid, "Minimum identifier length", _draft.Limits.MinIdentifierLength, 1m, 100m, 0, v => _draft.Limits.MinIdentifierLength = (int)v);
        AddNumber(grid, "Valid year from", _draft.Limits.ValidYearFrom, 1900m, 2200m, 0, v => _draft.Limits.ValidYearFrom = (int)v);
        AddNumber(grid, "Valid year to", _draft.Limits.ValidYearTo, 1900m, 2200m, 0, v => _draft.Limits.ValidYearTo = (int)v);
        AddNumber(grid, "Regex timeout (ms)", _draft.Limits.RegexTimeoutMilliseconds, 10m, 10000m, 0, v => _draft.Limits.RegexTimeoutMilliseconds = (int)v);
        return Scrolled(grid);
    }

    private Control ProfilesPage()
    {
        var grid = NewGrid();
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = "DisplayName",
            ValueMember = "Name"
        };
        combo.DataSource = _profiles.List();
        if (_draft.Profile.Length > 0)
        {
            combo.SelectedValue = _draft.Profile;
        }

        AddRow(grid, "Installed profiles", combo);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var apply = new Button { Text = "Use selected profile", AutoSize = true };
        apply.Click += (_, _) =>
        {
            if (combo.SelectedValue is not string name)
            {
                MessageBox.Show(this, "Pick a profile first.", "Parse rules", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                _profiles.Apply(name, _config);
                _config.Save();
                _pipeline.ReloadParser();
                ReloadDraft();
                ShowCategory();
                _status.Text = $"Profile '{name}' applied.";
            }
            catch (Exception ex)
            {
                _log.Error("Applying a parse profile from the dashboard failed", ex);
                MessageBox.Show(this, ex.Message, "Could not apply profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var import = new Button { Text = "Import a profile…", AutoSize = true };
        import.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Parse profile (*.json)|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var path = _profiles.Import(dialog.FileName, overwrite: true);
                combo.DataSource = _profiles.List();
                _status.Text = "Installed " + Path.GetFileName(path) + ".";
            }
            catch (Exception ex)
            {
                _log.Error("Importing a parse profile from the dashboard failed", ex);
                MessageBox.Show(this, ex.Message, "Could not import profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var saveAs = new Button { Text = "Save these rules as a profile…", AutoSize = true };
        saveAs.Click += (_, _) =>
        {
            if (_draft.ValidateRules().Count > 0)
            {
                MessageBox.Show(this, "Validate and fix the rules before saving them as a profile.",
                    "Parse rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "Parse profile (*.json)|*.json",
                FileName = string.IsNullOrWhiteSpace(_draft.Profile) ? "my-pos.json" : _draft.Profile + ".json"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var name = Path.GetFileNameWithoutExtension(dialog.FileName);
                var path = _profiles.Save(ParseProfileStore.Capture(name, name, _draft));
                combo.DataSource = _profiles.List();
                _status.Text = "Saved " + Path.GetFileName(path) + ".";
            }
            catch (Exception ex)
            {
                _log.Error("Saving a parse profile from the dashboard failed", ex);
                MessageBox.Show(this, ex.Message, "Could not save profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var openFolder = new Button { Text = "Open profiles folder", AutoSize = true };
        openFolder.Click += (_, _) => ShellOpen.Folder(_profiles.UserDirectory);

        buttons.Controls.Add(apply);
        buttons.Controls.Add(import);
        buttons.Controls.Add(saveAs);
        buttons.Controls.Add(openFolder);
        AddWideRow(grid, "Actions", buttons, 80);
        return Scrolled(grid);
    }

    private Control TestPage()
    {
        var grid = NewGrid();
        _sample.Dock = DockStyle.Fill;
        _sample.Multiline = true;
        _sample.AcceptsReturn = true;
        _sample.ScrollBars = ScrollBars.Vertical;
        _sample.Height = 240;
        _sample.PlaceholderText = "Paste the receipt text a print job delivered, then read it with these rules.";
        AddWideRow(grid, "Receipt text", _sample, 250);

        _result.Dock = DockStyle.Fill;
        _result.Multiline = true;
        _result.ReadOnly = true;
        _result.ScrollBars = ScrollBars.Vertical;
        _result.BackColor = Color.FromArgb(248, 250, 252);
        _result.Height = 200;
        AddWideRow(grid, "Reading", _result, 210);

        var parse = new Button { Text = "Read with these rules", AutoSize = true, MinimumSize = new Size(170, 32) };
        parse.Click += (_, _) =>
        {
            if (_sample.Text.Trim().Length == 0)
            {
                _result.Text = "Nothing to read yet.";
                return;
            }

            try
            {
                var errors = _draft.ValidateRules();
                if (errors.Count > 0)
                {
                    _result.Text = "Rules are not valid:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
                    return;
                }

                var reading = new ReceiptParser(_draft).Parse(_sample.Text);
                var lines = new List<string> { "Status: " + reading.Status };
                if (reading.Receipt is { } receipt)
                {
                    lines.Add($"Invoice: {receipt.InvoiceNumber ?? "-"}");
                    lines.Add($"Date: {receipt.Date:yyyy-MM-dd}");
                    lines.Add($"Type: {receipt.TransactionType}");
                    lines.Add($"Buyer: {receipt.Buyer.Name}");
                    lines.Add($"Items: {receipt.Items.Count}");
                    lines.Add($"Total: {receipt.Currency} {receipt.Total:0.00}");
                }

                if (!string.IsNullOrWhiteSpace(reading.Reason))
                {
                    lines.Add("Reason: " + reading.Reason);
                }

                lines.AddRange(reading.Warnings.Select(warning => "Warning: " + warning));
                _result.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                _result.Text = "The read failed: " + ex.Message;
            }
        };
        AddWideRow(grid, "", parse, 42);
        return Scrolled(grid);
    }

    private void ValidateDraft()
    {
        var errors = _draft.ValidateRules();
        _status.Text = errors.Count == 0
            ? "Rules are valid."
            : errors.Count + " rule problem(s) — check before saving.";
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Parse rules",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Revert()
    {
        ReloadDraft();
        ShowCategory();
        _status.Text = "Unsaved edits discarded.";
    }

    private void Save()
    {
        var errors = _draft.ValidateRules();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "These rules cannot be saved:" + Environment.NewLine +
                                  string.Join(Environment.NewLine, errors),
                "Parse rules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            CopyTo(_draft, _config.Parse);
            _config.Save();
            _pipeline.ReloadParser();
            _status.Text = "Rules saved. New print jobs use them now.";
        }
        catch (Exception ex)
        {
            _log.Error("Saving receipt rules from the dashboard failed", ex);
            MessageBox.Show(this, ex.Message, "Could not save rules", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReloadDraft()
    {
        var replacement = Clone(_config.Parse);
        foreach (var property in typeof(ParseConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite)
            {
                property.SetValue(_draft, property.GetValue(replacement));
            }
        }
    }

    private static ParseConfig Clone(ParseConfig source) =>
        JsonSerializer.Deserialize<ParseConfig>(JsonSerializer.Serialize(source)) ?? new ParseConfig();

    /// <summary>
    /// Writes into the live object rather than replacing it: the pipeline holds a reference to
    /// this instance, and swapping it would leave other components reading stale rules.
    /// </summary>
    private static void CopyTo(ParseConfig from, ParseConfig to)
    {
        foreach (var property in typeof(ParseConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            {
                property.SetValue(to, property.GetValue(from));
            }
        }
    }

    private static TableLayoutPanel NewGrid() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
        BackColor = Color.White
    };

    private static Panel Scrolled(TableLayoutPanel grid) => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        Controls = { grid }
    };

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, grid.RowCount - 1);
        grid.Controls.Add(control, 1, grid.RowCount - 1);
    }

    private static void AddWideRow(TableLayoutPanel grid, string label, Control control, int height)
    {
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, grid.RowCount - 1);
        grid.Controls.Add(control, 1, grid.RowCount - 1);
        grid.SetColumnSpan(control, 1);
    }

    private static void AddText(TableLayoutPanel grid, string label, string value, Action<string> changed, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            Multiline = multiline,
            AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            Dock = DockStyle.Fill
        };
        box.TextChanged += (_, _) => changed(box.Text);
        if (multiline)
        {
            AddWideRow(grid, label, box, 78);
        }
        else
        {
            AddRow(grid, label, box);
        }
    }

    private static void AddList(TableLayoutPanel grid, string label, IReadOnlyList<string> values, Action<List<string>> changed)
    {
        var box = new TextBox
        {
            Text = Join(values),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill
        };
        box.TextChanged += (_, _) => changed(Lines(box.Text));
        AddWideRow(grid, label, box, 84);
    }

    private static void AddBool(TableLayoutPanel grid, string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Text = label, Checked = value, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        box.CheckedChanged += (_, _) => changed(box.Checked);
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = " ", AutoSize = true }, 0, grid.RowCount - 1);
        grid.Controls.Add(box, 1, grid.RowCount - 1);
    }

    private static void AddNumber(
        TableLayoutPanel grid,
        string label,
        decimal value,
        decimal minimum,
        decimal maximum,
        int decimals,
        Action<decimal> changed)
    {
        var box = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Width = 160,
            Minimum = minimum,
            Maximum = maximum,
            DecimalPlaces = decimals,
            Value = Math.Min(maximum, Math.Max(minimum, value))
        };
        box.ValueChanged += (_, _) => changed(box.Value);
        AddRow(grid, label, box);
    }

    private static string Join(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    private static List<string> Lines(string? text) => (text ?? "")
        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    private static string CellText(object? value) => value?.ToString()?.Trim() ?? "";
}
