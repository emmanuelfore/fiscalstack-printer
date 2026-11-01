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
    private readonly InterceptorConfig _config;
    private readonly ILog _log;
    private readonly OfflineReceiptService _offline;
    private readonly SecretStore _secrets;
    private readonly ParseProfileStore _profiles;

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
    private readonly ComboBox _profile;
    private readonly Label _profileNote;
    private readonly CheckBox _offlineEnabled;
    private readonly Button _refresh;
    private readonly Label _status;

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
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(720, 600);

        _apiEndpoint = new TextBox { Text = config.ApiEndpoint, Dock = DockStyle.Fill };

        // The box is prefilled with the stored key - masked, like any password
        // field - so reopening the window shows what is in use. Leaving it
        // unchanged keeps the key, typing a value replaces it, clearing it and
        // confirming removes the key, so the credential still never lands in
        // config.json.
        _apiKey = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = "no key configured"
        };
        _keyHint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 2, 3, 0)
        };
        _clearKey = new Button { Text = "Remove stored key", AutoSize = true, Dock = DockStyle.Right };
        _clearKey.Click += RemoveStoredKey;
        _printingEnabled = new CheckBox
        {
            Text = "Print the fiscal receipt automatically",
            Checked = config.Printing.Enabled,
            AutoSize = true
        };

        _format = new ComboBox
        {
            Dock = DockStyle.Left,
            Width = 230,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
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

        _template = new ComboBox
        {
            Dock = DockStyle.Left,
            Width = 230,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _template.Items.AddRange(new object[]
        {
            "This app's fiscal layout",
            "The POS's own invoice + fiscal stamp"
        });
        _template.SelectedIndex = string.Equals(
            config.Printing.Template.Trim(), "original", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

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
            Width = 90
        };

        _profiles = ParseProfileStore.ForCurrentInstall();
        _profile = new ComboBox
        {
            Dock = DockStyle.Left,
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _profileNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray
        };

        var profileControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        var applyProfile = new Button { Text = "Use this profile", AutoSize = true };
        applyProfile.Click += (_, _) => ApplySelectedProfile();
        var importProfile = new Button { Text = "Import…", AutoSize = true };
        importProfile.Click += (_, _) => ImportProfile();
        var captureProfile = new Button { Text = "Save current rules as…", AutoSize = true };
        captureProfile.Click += (_, _) => SaveCurrentRulesAsProfile();
        var openProfiles = new Button { Text = "Open profiles folder", AutoSize = true };
        openProfiles.Click += (_, _) => ShellOpen.Folder(_profiles.UserDirectory);
        profileControls.Controls.Add(_profile);
        profileControls.Controls.Add(applyProfile);
        profileControls.Controls.Add(importProfile);
        profileControls.Controls.Add(captureProfile);
        profileControls.Controls.Add(openProfiles);

        var profileHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true
        };
        profileHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profileHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profileHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profileHost.Controls.Add(profileControls, 0, 0);
        _profileNote.Margin = new Padding(3, 6, 3, 0);
        profileHost.Controls.Add(_profileNote, 0, 1);
        _profile.SelectedIndexChanged += (_, _) => ShowProfileNote();

        _offlineEnabled = new CheckBox
        {
            Text = "Sign receipts offline when the API is unreachable",
            Checked = config.Offline.Enabled,
            AutoSize = true
        };

        _refresh = new Button { Text = "Refresh from FiscalStack", AutoSize = true };
        _refresh.Click += RefreshProvisioning;

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(51, 65, 85),
            Padding = new Padding(6),
            Text = "Not provisioned yet. Enable offline signing and press Refresh."
        };

        var statusHost = new Panel { Dock = DockStyle.Fill, Height = 96 };
        statusHost.Controls.Add(_status);

        _offlineEnabled.CheckedChanged += (_, _) => _refresh.Enabled = _offlineEnabled.Checked;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14, 14, 14, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var keyHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true
        };
        keyHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        keyHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyHost.Controls.Add(_apiKey, 0, 0);
        keyHost.SetColumnSpan(_apiKey, 2);
        keyHost.Controls.Add(_keyHint, 0, 1);
        keyHost.Controls.Add(_clearKey, 1, 1);

        AddRow(table, "API endpoint", _apiEndpoint);
        AddRow(table, "API key", keyHost);
        AddRow(table, "", Separator("Printing"));
        AddRow(table, "", _printingEnabled);
        AddRow(table, "Output format", _format);
        AddRow(table, "Customer copy", _template);
        AddRow(table, "Receipt printer (thermal)", _printerName);
        AddRow(table, "A4 invoice printer", _a4PrinterName);
        AddRow(table, "Paper width (mm)", _paperWidth);
        AddRow(table, "", Separator("Receipt parsing profile"));
        AddRow(table, "Layout rules", profileHost);
        AddRow(table, "", Separator("Offline fiscalisation"));
        AddRow(table, "", _offlineEnabled);
        AddRow(table, "", _refresh);
        AddRow(table, "Provisioned state", statusHost);

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = Color.DimGray,
            Text = "The API key is stored encrypted for this machine only, next to the configuration file " +
                   "in the secrets folder - it is never written back to config.json. Offline signing is " +
                   "provisioned from FiscalStack with that key: the ZIMRA device ID, RSA private key, tax " +
                   "buckets, QR base URL and receipt counters come from /api/zimra/offline-state, and the " +
                   "private key is cached in the same encrypted store.\n" +
                   "A new key or endpoint applies to the very next receipt; printing changes apply on save. " +
                   "Only enabling offline signing, or a different API timeout, needs a restart."
        };
        table.Controls.Add(new Label(), 0, table.RowCount);
        table.Controls.Add(note, 1, table.RowCount);
        table.RowCount++;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        var save = new Button { Text = "Save", Width = 100, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.Controls.Add(table);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(content, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(root);

        _refresh.Enabled = _offlineEnabled.Checked;
        UpdateKeyHint();
        UpdateStatusText();
        ReloadProfiles();
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

        return selector;
    }

    private static string ReadPrinterSelection(ComboBox selector) =>
        selector.SelectedItem is PrinterChoice choice ? choice.Value : selector.Text.Trim();

    private sealed record PrinterChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ProfileChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Refills the profile list from disk. <paramref name="selectName"/> is used right
    /// after an import or a capture, so the new profile is the one on screen.
    /// </summary>
    private void ReloadProfiles(string? selectName = null)
    {
        var profiles = _profiles.List();
        var wanted = selectName ?? _config.Parse.Profile;

        _profile.Items.Clear();
        foreach (var profile in profiles)
        {
            _profile.Items.Add(new ProfileChoice(
                profile.Name,
                profile.BuiltIn ? $"{profile.DisplayName}  (built-in)" : profile.DisplayName));
        }

        var match = _profile.Items.Cast<ProfileChoice>().FirstOrDefault(choice =>
            string.Equals(choice.Value, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _profile.SelectedItem = match;
        }
        else if (_profile.Items.Count > 0)
        {
            _profile.SelectedIndex = 0;
        }

        ShowProfileNote();
    }

    private void ShowProfileNote()
    {
        var lines = new List<string>();
        if (_profiles.LoadErrors.Count > 0)
        {
            lines.Add("Could not read: " + string.Join("; ", _profiles.LoadErrors));
        }

        if (_profile.SelectedItem is ProfileChoice choice)
        {
            var profile = _profiles.Find(choice.Value);
            if (profile is not null)
            {
                lines.Add(profile.Description);
                lines.Add(profile.BuiltIn
                    ? $"Shipped with the application, and already saved in config.json as profile '{_config.Parse.Profile}'."
                    : $"Installed at {profile.FilePath}.");
            }
        }
        else
        {
            lines.Add($"No profiles found. Drop a JSON profile into {_profiles.UserDirectory}, or save the rules " +
                       "this till is using as a profile.");
        }

        _profileNote.Text = string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    /// <summary>
    /// Writing the profile's rules into the configuration file is the whole action: the
    /// parser reads them when the service starts, so applying is saved immediately rather
    /// than waiting on the Save button the rest of this window uses.
    /// </summary>
    private void ApplySelectedProfile()
    {
        if (_profile.SelectedItem is not ProfileChoice choice)
        {
            MessageBox.Show("Pick a profile first.", "PrintInterceptor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var profile = _profiles.Apply(choice.Value, _config);
            _config.Save();
            _log.Info($"Receipt parsing now uses profile '{profile.Name}' from {profile.FilePath}; " +
                      "the rules were written to the configuration file.");
            MessageBox.Show(
                $"Using '{profile.DisplayName}'. Its rules are saved in {_config.ConfigPath}.\n\n" +
                "Restart PrintInterceptor (or the service) to parse with them; the receipts it has " +
                "already read are unaffected.",
                "Parsing profile applied",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not apply parse profile '{choice.Value}': {ex.Message}");
            MessageBox.Show(ex.Message, "Could not apply the profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a receipt parse profile",
            Filter = "Parse profile (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var installed = _profiles.Import(dialog.FileName);
            ReloadProfiles(Path.GetFileNameWithoutExtension(installed));
            _log.Info($"Imported parse profile to {installed}.");
            MessageBox.Show(
                $"Installed {Path.GetFileName(installed)}.\n\n" +
                "Press 'Use this profile' to take its rules into the configuration.",
                "Parse profile imported",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"A parse profile could not be imported: {ex.Message}");
            MessageBox.Show(ex.Message, "Could not import the profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Captures the rules this till is parsing with today, which is how a layout worked
    /// out at one till becomes a file another till imports. The file name becomes the
    /// profile name, so it is checked by the same rules as any imported profile.
    /// </summary>
    private void SaveCurrentRulesAsProfile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save this till's parsing rules as a profile",
            Filter = "Parse profile (*.json)|*.json",
            InitialDirectory = _profiles.UserDirectory,
            FileName = (string.IsNullOrWhiteSpace(_config.Parse.Profile) ? "my-pos" : _config.Parse.Profile) + ".json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var name = Path.GetFileNameWithoutExtension(dialog.FileName);
            var saved = _profiles.Save(ParseProfileStore.Capture(name, name, _config.Parse));
            ReloadProfiles(name);
            _log.Info($"Saved the current parsing rules as profile '{name}' to {saved}.");
            MessageBox.Show(
                $"Saved to {saved}.\n\nShare that file with another till and import it there.",
                "Profile saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"The current parsing rules could not be saved as a profile: {ex.Message}");
            MessageBox.Show(ex.Message, "Could not save the profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label Separator(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        Margin = new Padding(3, 12, 3, 3)
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        if (label.Length > 0)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left
            }, 0, row);
        }
        else
        {
            table.Controls.Add(new Label(), 0, row);
        }

        table.Controls.Add(control, 1, row);
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
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _log.Error("Could not save PrintInterceptor settings", ex);
            MessageBox.Show(ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
