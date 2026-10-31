using System.Drawing;
using System.Windows.Forms;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;

namespace PrintInterceptor;

/// <summary>
/// Fiscal-device screen: live status, registration card and day open/close.
/// Mirrors the Fiscal Device page in FiscalBridge, using the same API-key
/// scoped endpoints so the POS never needs a device id.
/// </summary>
public sealed class FiscalStatusForm : Form
{
    private readonly InterceptorConfig _config;
    private readonly FiscalStackClient _client;
    private readonly CompanyProfileService _company;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();

    private readonly Label _companyName;
    private readonly Label _companyIdent;
    private readonly Label _companyAddress;
    private readonly PictureBox _logo;

    private readonly Label _connection;
    private readonly Label _deviceId;
    private readonly Label _serialNumber;
    private readonly Label _dayNumber;
    private readonly Label _dayStatus;
    private readonly Label _globalReceipt;
    private readonly Label _dayOpened;
    private readonly Label _closeError;
    private readonly Label _checkedAt;

    private readonly Label _cardTin;
    private readonly Label _cardVat;
    private readonly Label _cardBpn;
    private readonly Label _cardCompany;
    private readonly Label _cardAddress;
    private readonly Label _cardRegistration;
    private readonly Label _cardSerial;
    private readonly Label _cardLogoUrl;

    private readonly Button _refresh;
    private readonly Button _openDay;
    private readonly Button _closeDay;
    private readonly Label _busy;
    private readonly System.Windows.Forms.Timer _autoRefresh;
    private bool _loading;

    public FiscalStatusForm(
        InterceptorConfig config,
        FiscalStackClient client,
        CompanyProfileService company,
        ILog log)
    {
        _config = config;
        _client = client;
        _company = company;
        _log = log;

        Text = "Fiscal status";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(660, 590);
        MinimumSize = new Size(560, 480);
        Font = new Font("Segoe UI", 9f);

        _companyName = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(16, 12)
        };
        _companyIdent = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(16, 42)
        };
        _companyAddress = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(16, 62)
        };
        _logo = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(104, 64),
            Location = new Point(520, 12),
            BackColor = Color.Transparent
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.White,
            Padding = new Padding(4)
        };
        header.Controls.Add(_companyName);
        header.Controls.Add(_companyIdent);
        header.Controls.Add(_companyAddress);
        header.Controls.Add(_logo);

        _connection = ValueLabel();
        _deviceId = ValueLabel();
        _serialNumber = ValueLabel();
        _dayNumber = ValueLabel();
        _dayStatus = ValueLabel(bold: true);
        _globalReceipt = ValueLabel();
        _dayOpened = ValueLabel();
        _closeError = ValueLabel();
        _checkedAt = ValueLabel();

        var statusTab = new TabPage("Status") { Padding = new Padding(12) };
        var statusGrid = Grid();
        AddRow(statusGrid, "Connection", _connection, bold: true);
        AddRow(statusGrid, "Device ID", _deviceId);
        AddRow(statusGrid, "Serial number", _serialNumber);
        AddRow(statusGrid, "Fiscal day", _dayNumber);
        AddRow(statusGrid, "Day status", _dayStatus);
        AddRow(statusGrid, "Last receipt (global)", _globalReceipt);
        AddRow(statusGrid, "Day opened", _dayOpened);
        AddRow(statusGrid, "Close error", _closeError);
        AddRow(statusGrid, "Last checked", _checkedAt);

        // Which printer/till this window belongs to: one instance serves one
        // capture port and one real receipt printer.
        var format = _config.Printing.Format switch
        {
            "a4" => "A4 invoice",
            "auto" => $"auto · thermal {_config.Printing.PaperWidthMm} mm or A4",
            _ => $"thermal · {_config.Printing.PaperWidthMm} mm"
        };
        AddRow(statusGrid, "Capture port", FixedLabel($"{_config.ListenAddress}:{_config.ListenPort} (virtual printer)"));
        AddRow(statusGrid, "Receipt printer", FixedLabel(
            string.IsNullOrWhiteSpace(_config.Printing.PrinterName)
                ? "Windows default"
                : _config.Printing.PrinterName));
        if (_config.Printing.Format is "a4" or "auto")
        {
            AddRow(statusGrid, "A4 printer", FixedLabel(
                string.IsNullOrWhiteSpace(_config.Printing.A4PrinterName)
                    ? "Falls back to the receipt printer"
                    : _config.Printing.A4PrinterName));
        }
        AddRow(statusGrid, "Print format", FixedLabel(format));
        AddRow(statusGrid, "FiscalStack API", FixedLabel(_config.ApiEndpoint));
        statusTab.Controls.Add(statusGrid);

        _cardTin = ValueLabel();
        _cardVat = ValueLabel();
        _cardBpn = ValueLabel();
        _cardCompany = ValueLabel();
        _cardAddress = ValueLabel();
        _cardRegistration = ValueLabel();
        _cardSerial = ValueLabel();
        _cardLogoUrl = ValueLabel();

        var cardTab = new TabPage("Card details") { Padding = new Padding(12) };
        var cardGrid = Grid();
        AddRow(cardGrid, "TIN", _cardTin);
        AddRow(cardGrid, "VAT number", _cardVat);
        AddRow(cardGrid, "BPN", _cardBpn);
        AddRow(cardGrid, "Company", _cardCompany);
        AddRow(cardGrid, "Address", _cardAddress);
        AddRow(cardGrid, "Registration number", _cardRegistration);
        AddRow(cardGrid, "Serial number", _cardSerial);
        AddRow(cardGrid, "Logo URL", _cardLogoUrl);
        cardTab.Controls.Add(cardGrid);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(statusTab);
        tabs.TabPages.Add(cardTab);

        _refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(92, 30) };
        _refresh.Click += async (_, _) => await RefreshAsync();

        _openDay = new Button { Text = "Open fiscal day", AutoSize = true, MinimumSize = new Size(118, 30) };
        _openDay.Click += async (_, _) => await OpenDayAsync();

        _closeDay = new Button { Text = "Close fiscal day", AutoSize = true, MinimumSize = new Size(118, 30) };
        _closeDay.Click += async (_, _) => await CloseDayAsync();

        _busy = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = "",
            Anchor = AnchorStyles.Left
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 10, 10, 10),
            WrapContents = false
        };
        buttons.Controls.Add(_closeDay);
        buttons.Controls.Add(_openDay);
        buttons.Controls.Add(_refresh);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 4, 14, 0) };
        footer.Controls.Add(_busy);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(buttons);
        Controls.Add(header);

        AcceptButton = _refresh;
        _autoRefresh = new System.Windows.Forms.Timer { Interval = 20_000 };
        _autoRefresh.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            _autoRefresh.Start();
            await RefreshAsync();
        };
        FormClosed += (_, _) =>
        {
            _autoRefresh.Stop();
            _cts.Cancel();
        };

        ApplyCompany();
        _openDay.Enabled = false;
        _closeDay.Enabled = false;
    }

    private void ApplyCompany()
    {
        _companyName.Text = string.IsNullOrWhiteSpace(_company.Name) ? "Fiscal device" : _company.Name;
        _companyIdent.Text = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(_company.Tin) ? null : $"TIN {_company.Tin}",
            string.IsNullOrWhiteSpace(_company.VatNumber) ? null : $"VAT {_company.VatNumber}"
        }.Where(value => value is not null));
        _companyAddress.Text = _company.Address;

        try
        {
            var logoPath = _company.ResolveLogoPath(
                string.IsNullOrWhiteSpace(_config.Company.LogoPath) ? null : _config.Company.LogoPath);
            if (logoPath is not null && File.Exists(logoPath))
            {
                using var stream = new FileStream(logoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _logo.Image = Image.FromStream(stream);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not preview the company logo: {ex.Message}");
        }
    }

    private async Task RefreshAsync()
    {
        if (IsDisposed || _loading)
        {
            return;
        }

        _loading = true;
        SetBusy("Checking FiscalStack and the fiscal gateway…", busy: true);

        try
        {
            var statusTask = _client.GetDeviceStatusAsync(_cts.Token);
            var cardTask = _client.GetDeviceDetailsAsync(_cts.Token);
            await Task.WhenAll(statusTask, cardTask);

            ApplyStatus(await statusTask);
            ApplyCard(await cardTask);
            _checkedAt.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            _log.Warn($"Fiscal status refresh failed: {ex.Message}");
            if (!IsDisposed)
            {
                _connection.Text = "Unreachable — " + ex.Message;
                _connection.ForeColor = Color.FromArgb(185, 28, 28);
            }
        }
        finally
        {
            _loading = false;
            SetBusy("", busy: false);
        }
    }

    private void ApplyStatus(DeviceStatusResult result)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!result.Success || result.Status is null)
        {
            _connection.Text = "Unreachable — " + (result.Error ?? "no response");
            _connection.ForeColor = Color.FromArgb(185, 28, 28);
            _dayStatus.Text = "Unknown";
            _dayStatus.ForeColor = Color.FromArgb(100, 116, 139);
            _openDay.Enabled = false;
            _closeDay.Enabled = false;
            return;
        }

        var status = result.Status;
        _connection.Text = "Reachable";
        _connection.ForeColor = Color.FromArgb(21, 128, 61);

        _deviceId.Text = OrDash(status.DeviceId);
        _serialNumber.Text = OrDash(status.SerialNumber);
        _dayNumber.Text = status.FiscalDayNo?.ToString() ?? "—";
        _dayOpened.Text = status.FiscalDayOpenedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        _globalReceipt.Text = status.LastReceiptGlobalNo?.ToString() ?? "—";
        _closeError.Text = status.FiscalDayClosingErrorCode is null
            ? "None"
            : $"{status.FiscalDayClosingErrorCode} — {DescribeCloseError(status.FiscalDayClosingErrorCode)}";

        _dayStatus.Text = DescribeDayStatus(status);
        _dayStatus.ForeColor = status.IsClosePending
            ? Color.FromArgb(180, 83, 9)
            : status.IsDayOpen
                ? Color.FromArgb(21, 128, 61)
                : Color.FromArgb(71, 85, 105);

        _openDay.Enabled = !status.IsDayOpen;
        _closeDay.Enabled = status.IsDayOpen;
    }

    private void ApplyCard(DeviceCardResult result)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!result.Success || result.Card is null)
        {
            // The cached company identity still gives the operator usable values.
            _cardTin.Text = OrDash(_company.Tin);
            _cardVat.Text = OrDash(_company.VatNumber);
            _cardBpn.Text = "—";
            _cardCompany.Text = OrDash(_company.Name);
            _cardAddress.Text = OrDash(_company.Address);
            _cardRegistration.Text = "—";
            _cardSerial.Text = "—";
            _cardLogoUrl.Text = result.Error ?? "—";
            _cardLogoUrl.ForeColor = Color.FromArgb(180, 83, 9);
            return;
        }

        var card = result.Card;
        _cardTin.Text = OrDash(card.Tin);
        _cardVat.Text = OrDash(card.Vat);
        _cardBpn.Text = OrDash(card.Bpn);
        _cardCompany.Text = OrDash(card.CompanyName);
        _cardAddress.Text = OrDash(card.Address);
        _cardRegistration.Text = OrDash(card.RegistrationNumber);
        _cardSerial.Text = OrDash(card.SerialNumber);
        _cardLogoUrl.Text = OrDash(card.LogoUrl);
        _cardLogoUrl.ForeColor = Color.FromArgb(30, 41, 59);
    }

    private async Task OpenDayAsync()
    {
        if (_loading)
        {
            return;
        }

        // Stop the auto-refresh while the confirmation is open so the day buttons
        // cannot change state under the dialog.
        _loading = true;
        var answer = MessageBox.Show(
            "Open a new fiscal day?" + Environment.NewLine + Environment.NewLine +
            "The previous day must already be closed. New receipts will use the next fiscal day number.",
            "Open fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            _loading = false;
            return;
        }

        SetBusy("Opening the fiscal day…", busy: true);
        try
        {
            var result = await _client.OpenFiscalDayAsync(_cts.Token);
            if (IsDisposed)
            {
                return;
            }

            if (result.Success)
            {
                _log.Info($"Fiscal day opened from PrintInterceptor. {result.Message}");
                MessageBox.Show(result.Message, "Fiscal day opened",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _log.Warn($"Opening the fiscal day failed: {result.Message}");
                MessageBox.Show(result.Message, "Could not open the fiscal day",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loading = false;
            SetBusy("", busy: false);
        }

        await RefreshAsync();
    }

    private async Task CloseDayAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        var answer = MessageBox.Show(
            "Close the fiscal day?" + Environment.NewLine + Environment.NewLine +
            "Every receipt for the day must be fiscalised; missing or rejected receipts (grey/red) " +
            "make the gateway reject the close. Closure can take up to a minute." + Environment.NewLine +
            Environment.NewLine + "Close the fiscal day now?",
            "Close fiscal day",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            _loading = false;
            return;
        }

        SetBusy("Submitting the close and waiting for confirmation…", busy: true);
        try
        {
            var result = await _client.CloseFiscalDayAsync(_cts.Token);
            if (IsDisposed)
            {
                return;
            }

            if (result.Success)
            {
                _log.Info($"Fiscal day closed from PrintInterceptor. {result.Message}");
                MessageBox.Show(result.Message, "Fiscal day closed",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                var detail = result.ErrorCode is null
                    ? result.Message
                    : $"{result.Message}{Environment.NewLine}{Environment.NewLine}" +
                      $"Gateway error: {result.ErrorCode} — {DescribeCloseError(result.ErrorCode)}";

                _log.Warn($"Closing the fiscal day failed: {result.Message} ({result.ErrorCode ?? "no code"})");
                MessageBox.Show(detail,
                    result.Pending ? "Closure not confirmed yet" : "Could not close the fiscal day",
                    MessageBoxButtons.OK,
                    result.Pending ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loading = false;
            SetBusy("", busy: false);
        }

        await RefreshAsync();
    }

    private void SetBusy(string text, bool busy)
    {
        if (IsDisposed)
        {
            return;
        }

        _busy.Text = text;
        _refresh.Enabled = !busy;
        if (busy)
        {
            _openDay.Enabled = false;
            _closeDay.Enabled = false;
        }
    }

    private static string DescribeDayStatus(FiscalDeviceStatus status) => status.FiscalDayStatus switch
    {
        "FiscalDayOpened" => "Open",
        "FiscalDayClosed" => "Closed",
        "FiscalDayCloseFailed" => "Open — previous close failed, retry the close",
        "FiscalDayCloseInitiated" => "Closing… (waiting for the gateway)",
        "" => "Unknown",
        _ => status.FiscalDayStatus
    };

    internal static string DescribeCloseError(string? code) => code switch
    {
        "MissingReceipts" => "one or more receipts are missing from the day's sequence (grey receipts).",
        "ReceiptsWithValidationErrors" => "the day has receipts with validation errors (red receipts).",
        "BadCertificateSignature" => "the device certificate signature was rejected.",
        "CountersMismatch" => "the submitted totals do not match the device counters.",
        "UnknownError" => "the gateway did not explain the failure.",
        _ => string.IsNullOrWhiteSpace(code) ? "unknown" : "the gateway rejected the close."
    };

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value!;

    private static Label ValueLabel(bool bold = false) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(400, 0),
        ForeColor = Color.FromArgb(30, 41, 59),
        Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular),
        Anchor = AnchorStyles.Left,
        Text = "—"
    };

    private static Label FixedLabel(string text) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(400, 0),
        ForeColor = Color.FromArgb(30, 41, 59),
        Font = new Font("Segoe UI", 9f),
        Anchor = AnchorStyles.Left,
        Text = text
    };

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            Padding = new Padding(2)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Label value, bool bold = false)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(3, 7, 3, 7)
        }, 0, row);
        value.Margin = new Padding(3, 7, 3, 7);
        grid.Controls.Add(value, 1, row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logo.Image?.Dispose();
            _autoRefresh.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
