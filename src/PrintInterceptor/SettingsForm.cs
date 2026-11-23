using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.State;

namespace PrintInterceptor;

public sealed class SettingsForm : Form
{
    private bool _embeddedMode;

    /// <summary>
    /// Saved from inside the dashboard: stay open and tell the host instead of closing the
    /// dialog. First-run and --configure-only still use the normal modal behaviour.
    /// </summary>
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
            TopMost = false;
            TopLevel = false;
            Dock = DockStyle.Fill;
            _cancel.DialogResult = DialogResult.None;
            _cancel.Text = "Done";
        }
    }

    public event EventHandler? SettingsSaved;

    public event EventHandler? CloseRequested;

    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly OfflineReceiptService _offline;
    private readonly SecretStore _secrets;

    private readonly TextBox _apiEndpoint;
    private readonly TextBox _apiKey;
    private readonly Label _keyHint;
    private readonly Button _clearKey;
    private readonly CheckBox _printingEnabled;
    private readonly ComboBox _format;
    private readonly ComboBox _template;
    private readonly ComboBox _printerName;
    private readonly ComboBox _a4PrinterName;
    private readonly NumericUpDown _paperWidth;
    private readonly CheckBox _offlineEnabled;
    private readonly Button _refresh;
    private readonly Label _status;
    private readonly Button _cancel;

    public SettingsForm(
        InterceptorConfig config,
        ILog log,
        OfflineReceiptService offline,
        SecretStore secrets)
    {
        _config = config;
        _log = log;
        _offline = offline;
        _secrets = secrets;

        Text = "PrintInterceptor settings";
        UiTheme.UseAppIcon(this);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Ui(9f);
        ClientSize = new Size(900, 700);
        MinimumSize = new Size(780, 600);

        _apiEndpoint = new TextBox { Text = config.ApiEndpoint, Dock = DockStyle.Fill };
        UiTheme.Input(_apiEndpoint);
        UiTheme.Tip(_apiEndpoint, "The FiscalStack gateway URL, including the trailing slash.");

        // The box is prefilled with the stored key - masked, like any password
        // field - so reopening the window shows what is in use. Leaving it
        // unchanged keeps the key, typing a value replaces it, clearing it and
        // confirming removes the key, so the credential still never lands in
        // config.json.
        _apiKey = new TextBox { Dock = DockStyle.Fill };
        UiTheme.Password(_apiKey, "no key configured");
        UiTheme.Tip(_apiKey, "The key is saved in Windows Credential Manager, never in config.json.");
        _keyHint = UiTheme.Subheading("");
        _keyHint.Margin = new Padding(2, 2, 2, 2);
        _clearKey = new Button { Text = "Remove stored key", AutoSize = true };
        UiTheme.Secondary(_clearKey);
        _clearKey.Click += RemoveStoredKey;
        var showKey = new Button { Text = "Show", AutoSize = true };
        UiTheme.Secondary(showKey);
        showKey.Click += (_, _) =>
        {
            _apiKey.UseSystemPasswordChar = !_apiKey.UseSystemPasswordChar;
            showKey.Text = _apiKey.UseSystemPasswordChar ? "Show" : "Hide";
        };

        _printingEnabled = new CheckBox
        {
            Text = "Print the fiscal receipt automatically",
            Checked = config.Printing.Enabled,
            AutoSize = true
        };
        UiTheme.Check(_printingEnabled);

        _format = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _format.Items.AddRange(new object[]
        {
            "Thermal receipt (58/80 mm)",
            "A4 invoice",
            "Auto (A4 for PDF print jobs)"
        });
        _format.SelectedIndex = config.Printing.Format.Trim().ToLowerInvariant() switch
        {
            "a4" => 1,
            "auto" => 2,
            _ => 0
        };
        UiTheme.Input(_format);

        _template = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _template.Items.AddRange(new object[]
        {
            "This app's fiscal layout",
            "The POS's own invoice + fiscal stamp"
        });
        _template.SelectedIndex = string.Equals(
            config.Printing.Template.Trim(), "original", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UiTheme.Input(_template);
        UiTheme.Tip(_template, "The POS's own invoice keeps the original copy on file, so job retention is required.");

        _printerName = CreatePrinterSelector(
            config.Printing.PrinterName,
            "Windows default printer");
        _a4PrinterName = CreatePrinterSelector(
            config.Printing.A4PrinterName,
            "Same as receipt printer, then Windows default");
        _paperWidth = new NumericUpDown
        {
            Minimum = 40,
            Maximum = 120,
            Value = Math.Clamp(config.Printing.PaperWidthMm, 40, 120),
            Dock = DockStyle.Left,
            Width = 100
        };
        UiTheme.Input(_paperWidth);

        _offlineEnabled = new CheckBox
        {
            Text = "Sign receipts offline when the API is unreachable",
            Checked = config.Offline.Enabled,
            AutoSize = true
        };
        UiTheme.Check(_offlineEnabled);

        _refresh = new Button { Text = "Refresh from FiscalStack", AutoSize = true };
        UiTheme.Secondary(_refresh);
        _refresh.Click += RefreshProvisioning;

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 104,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Canvas,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Ui(8.5f),
            Padding = new Padding(8, 6, 8, 6),
            Text = "Not provisioned yet. Enable offline signing and press Refresh."
        };

        _offlineEnabled.CheckedChanged += (_, _) => _refresh.Enabled = _offlineEnabled.Checked;

        var keyButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        keyButtons.Controls.Add(_clearKey);
        keyButtons.Controls.Add(showKey);

        var keyHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3
        };
        keyHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyHost.Controls.Add(_apiKey, 0, 0);
        keyHost.Controls.Add(keyButtons, 0, 1);
        keyHost.Controls.Add(_keyHint, 0, 2);

        var connection = Card("Connection", new[]
        {
            Row("API endpoint", _apiEndpoint),
            Row("API key", keyHost)
        }, "The API key stays in the Windows credential store. Show is only for checking what is registered on this till.");

        var printing = Card("Printing", new[]
        {
            Row("", _printingEnabled),
            Row("Output format", _format),
            Row("Customer copy", _template),
            Row("Receipt printer (thermal)", _printerName),
            Row("A4 invoice printer", _a4PrinterName),
            Row("Paper width (mm)", _paperWidth)
        });

        var provisioning = Card("Offline fiscalisation", new[]
        {
            Row("", _offlineEnabled),
            Row("", _refresh),
            Row("Provisioned state", _status)
        }, "Offline signing needs a registered device and tax codes from FiscalStack before the first sale.");

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 12, 16, 12) };
        content.Controls.Add(provisioning);
        content.Controls.Add(printing);
        content.Controls.Add(connection);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = UiTheme.Surface,
            Padding = new Padding(16)
        };
        var save = new Button { Text = "Save", MinimumSize = new Size(120, 36), DialogResult = DialogResult.None };
        UiTheme.Primary(save);
        _cancel = new Button { Text = "Cancel", MinimumSize = new Size(120, 36), DialogResult = DialogResult.Cancel };
        UiTheme.Secondary(_cancel);
        _cancel.Click += (_, _) =>
        {
            if (EmbeddedMode)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(save);
        buttons.Controls.Add(_cancel);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 66,
            BackColor = UiTheme.Surface,
            Padding = new Padding(18, 10, 18, 6)
        };
        var heading = UiTheme.Heading("Settings");
        heading.Dock = DockStyle.Top;
        var subheading = UiTheme.Subheading(
            "Gateway, printing and offline provisioning for this till. Parse rules are edited on their own page.");
        subheading.Dock = DockStyle.Top;
        subheading.Padding = new Padding(0, 4, 0, 0);
        header.Controls.Add(subheading);
        header.Controls.Add(heading);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = save;
        CancelButton = _cancel;
        Controls.Add(root);

        _refresh.Enabled = _offlineEnabled.Checked;
        UpdateKeyHint();
        UpdateStatusText();
    }

    /// <summary>
    /// Shows where the live key came from without revealing it, so an operator can
    /// tell a registered till from one still reading a stale environment variable.
    /// </summary>
    private void UpdateKeyHint()
    {
        var stored = _secrets.TryGet(SecretStore.ApiKeyEntry, out var value);
        if (stored)
        {
            _keyPrefill = value;
            if (_apiKey.Text.Length == 0)
            {
                _apiKey.Text = value;
            }

            _keyHint.Text = $"Registered on this till: {SecretStore.Describe(value)}";
            _clearKey.Enabled = true;
            return;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyResolver.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            _keyPrefill = fromEnvironment;
            if (_apiKey.Text.Length == 0)
            {
                _apiKey.Text = fromEnvironment;
            }

            _keyHint.Text = $"In use from {ApiKeyResolver.EnvironmentVariable}: {SecretStore.Describe(fromEnvironment)}";
            _clearKey.Enabled = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _keyPrefill = _config.ApiKey;
            if (_apiKey.Text.Length == 0)
            {
                _apiKey.Text = _config.ApiKey;
            }

            _keyHint.Text = $"Still in config.json: {SecretStore.Describe(_config.ApiKey)} - saving moves it to the secret store";
            _clearKey.Enabled = false;
            return;
        }

        _keyPrefill = "";
        _keyHint.Text = "No key configured; receipts will be held for review.";
        _clearKey.Enabled = false;
    }

    /// <summary>
    /// The key that was prefilled into the box at startup (from the store, the
    /// environment, or config.json). Saving with the box unchanged does not touch
    /// it; clearing the field means the key is removed once the operator confirms.
    /// </summary>
    private string _keyPrefill = "";

    private void RemoveStoredKey(object? sender, EventArgs e)
    {
        if (MessageBox.Show(
                "Remove the API key registered on this till? Receipts stop being fiscalised until a new one is saved.",
                "PrintInterceptor",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        if (!_secrets.TryRemove(SecretStore.ApiKeyEntry, out var error))
        {
            MessageBox.Show(error, "Could not remove the key", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _config.ApiKey = "";
        _keyPrefill = "";
        _apiKey.Clear();
        UpdateKeyHint();
        _log.Warn("The API key was removed from this till through the settings window.");
    }

    private void UpdateStatusText()
    {
        var state = _offline.RemoteState;
        if (state is null)
        {
            _status.Text = _config.Offline.Enabled
                ? "No device state loaded yet. Press Refresh from FiscalStack."
                : "Offline signing is disabled.";
            return;
        }

        var taxes = string.Join(", ", new[]
        {
            DescribeTax("STANDARD", state.Taxes?.Standard),
            DescribeTax("ZERO_RATED", state.Taxes?.ZeroRated),
            DescribeTax("EXEMPT", state.Taxes?.Exempt)
        }.Where(value => value is not null));

        _status.Text =
            $"Device: {_offline.EffectiveDeviceId}   Serial: {OrDash(state.DeviceSerialNo)}{Environment.NewLine}" +
            $"Fiscal day: {state.CurrentFiscalDayNo} ({(state.FiscalDayOpen ? "open" : "closed")})   " +
            $"Counter: {state.DailyReceiptCount}   Global no: {state.LastReceiptGlobalNo}{Environment.NewLine}" +
            $"Key: {_offline.KeySourceText}" +
            (_offline.ProvisionedAt is { } at ? $"   Updated: {at:yyyy-MM-dd HH:mm}" : "") +
            $"{Environment.NewLine}Taxes: {(taxes.Length > 0 ? taxes : "not returned")}";
    }

    private static string? DescribeTax(string type, OfflineStateTax? tax) =>
        tax is null
            ? null
            : $"{type}={tax.TaxCode}/{tax.TaxId}" +
              (tax.TaxPercent > 0 ? $"@{tax.TaxPercent:0.##}%" : "");

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private async void RefreshProvisioning(object? sender, EventArgs e)
    {
        if (!_config.Offline.Enabled && !_offlineEnabled.Checked)
        {
            MessageBox.Show("Enable offline signing first, then refresh.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var endpoint = _apiEndpoint.Text.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("The API endpoint must be an absolute http(s) URL.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _refresh.Enabled = false;
        _status.Text = "Contacting FiscalStack…";

        try
        {
            // The service reads this from the shared config, so mirror the form
            // before asking it to provision. An empty key box means "use what is
            // already registered on this till".
            _config.Offline.Enabled = _offlineEnabled.Checked;

            var probeKey = _apiKey.Text.Trim();
            var probeConfig = new InterceptorConfig
            {
                ApiEndpoint = endpoint.EndsWith('/') ? endpoint : endpoint + "/",
                ApiKey = probeKey.Length > 0 ? probeKey : _config.ApiKey,
                ApiTimeoutSeconds = _config.ApiTimeoutSeconds
            };

            using var client = new FiscalStackClient(probeConfig, _log);
            var refreshed = await _offline.RefreshStateAsync(client, CancellationToken.None);
            if (!refreshed)
            {
                _status.Text = "FiscalStack did not return usable ZIMRA state. Check the API key and " +
                               "endpoint. Details are in the log.";
                return;
            }

            UpdateStatusText();
        }
        catch (Exception ex)
        {
            _log.Error("Could not provision offline fiscalisation from FiscalStack", ex);
            _status.Text = $"Provisioning failed: {ex.Message}";
        }
        finally
        {
            _refresh.Enabled = _offlineEnabled.Checked;
        }
    }

    /// <summary>
    /// Lists the printers installed on this till while retaining free text so an
    /// existing configuration is not lost if its printer is temporarily offline.
    /// </summary>
    private ComboBox CreatePrinterSelector(string configuredName, string fallbackLabel)
    {
        var selector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        selector.Items.Add(new PrinterChoice("", fallbackLabel));

        try
        {
            var defaultPrinter = new PrinterSettings().PrinterName;
            var printers = PrinterSettings.InstalledPrinters.Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            foreach (var printer in printers)
            {
                var label = string.Equals(printer, defaultPrinter, StringComparison.OrdinalIgnoreCase)
                    ? $"{printer} (Windows default)"
                    : printer;
                selector.Items.Add(new PrinterChoice(printer, label));
            }
        }
        catch (Exception ex)
        {
            // Printer enumeration depends on the Windows spooler. Settings must
            // still open so the API key and other configuration can be repaired.
            _log.Warn($"Could not enumerate installed printers: {ex.Message}");
        }

        var configured = configuredName.Trim();
        var match = selector.Items.Cast<PrinterChoice>().FirstOrDefault(choice =>
            string.Equals(choice.Value, configured, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            selector.SelectedItem = match;
        }
        else
        {
            selector.Text = configured.Length > 0 ? configured : fallbackLabel;
        }

        UiTheme.Input(selector);
        UiTheme.Tip(selector, "Installed Windows printers. A configured name is kept if its printer is temporarily offline.");
        return selector;
    }

    private static string ReadPrinterSelection(ComboBox selector) =>
        selector.SelectedItem is PrinterChoice choice ? choice.Value : selector.Text.Trim();

    private sealed record PrinterChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static (string Label, Control Control) Row(string label, Control control) => (label, control);

    private static CardPanel Card(string title, (string Label, Control Control)[] rows, string? note = null)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };

        var rowCount = rows.Length + (note is null ? 0 : 1);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = UiTheme.SectionLabel(UiTheme.SafeText(rows[i].Label));
            label.Margin = new Padding(0, 8, 10, 0);
            label.Anchor = AnchorStyles.Left;
            table.Controls.Add(label, 0, i);
            rows[i].Control.Margin = new Padding(0, 4, 0, 4);
            table.Controls.Add(rows[i].Control, 1, i);
        }

        if (note is not null)
        {
            var noteRow = rows.Length;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var noteLabel = UiTheme.Subheading(UiTheme.SafeText(note));
            noteLabel.Margin = new Padding(0, 8, 0, 0);
            table.SetColumnSpan(noteLabel, 2);
            table.Controls.Add(noteLabel, 0, noteRow);
        }

        card.Controls.Add(table);
        var caption = UiTheme.Heading(UiTheme.SafeText(title));
        caption.Dock = DockStyle.Top;
        caption.Font = UiTheme.Ui(11f, FontStyle.Bold);
        caption.Margin = new Padding(0, 0, 0, 8);
        card.Controls.Add(caption);
        return card;
    }

    private void SaveSettings()
    {
        var endpoint = _apiEndpoint.Text.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("The API endpoint must be an absolute http(s) URL.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The box is prefilled with the key in use, so what matters at save time is
        // whether the operator edited it: an unchanged value is left exactly where
        // it is, a new one is committed to the secret store first - the value only
        // becomes live for this process once it is safely on disk, and
        // InterceptorConfig.Save cannot write it to config.json either way - and an
        // intentionally cleared box removes the key after a confirmation.
        var typedKey = _apiKey.Text.Trim();
        if (typedKey.Length > 0 && typedKey != _keyPrefill)
        {
            if (!_secrets.TrySet(SecretStore.ApiKeyEntry, typedKey, out var keyError))
            {
                MessageBox.Show(
                    $"The API key could not be stored securely, so it was not applied:{Environment.NewLine}{keyError}",
                    "PrintInterceptor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }
        else if (typedKey.Length == 0 && _keyPrefill.Length > 0)
        {
            if (MessageBox.Show(
                    "The API key field is empty. Remove the key this till is using? Receipts stop being fiscalised until a new one is saved.",
                    "PrintInterceptor",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _apiKey.Text = _keyPrefill;
                return;
            }

            if (_secrets.Contains(SecretStore.ApiKeyEntry) &&
                !_secrets.TryRemove(SecretStore.ApiKeyEntry, out var removeError))
            {
                MessageBox.Show(
                    $"The API key could not be removed:{Environment.NewLine}{removeError}",
                    "PrintInterceptor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _keyPrefill = "";
            _config.ApiKey = "";
        }

        _config.ApiEndpoint = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
        if (typedKey.Length > 0)
        {
            _config.ApiKey = typedKey;
        }

        _config.Printing.Enabled = _printingEnabled.Checked;
        _config.Printing.Format = _format.SelectedIndex switch
        {
            1 => "a4",
            2 => "auto",
            _ => "thermal"
        };
        _config.Printing.Template = _template.SelectedIndex == 1 ? "original" : "fiscal";
        // The stamped copy is printed from the captured job's own bytes, so keeping
        // them is not optional once "the POS's own invoice" is chosen.
        if (_config.Printing.Template == "original")
        {
            _config.Storage.RetainJobs = true;
        }

        _config.Printing.PrinterName = ReadPrinterSelection(_printerName);
        _config.Printing.A4PrinterName = ReadPrinterSelection(_a4PrinterName);
        _config.Printing.PaperWidthMm = (int)_paperWidth.Value;
        _config.Offline.Enabled = _offlineEnabled.Checked;
        _config.SetupCompleted = true;

        try
        {
            _config.Save();
            UpdateKeyHint();
            if (EmbeddedMode)
            {
                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not save PrintInterceptor settings", ex);
            MessageBox.Show(ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
