namespace PrintInterceptor.Core.Fiscalisation;

/// <summary>
/// Live fiscal-device state from <c>GET /api/zimra/device-status</c>. The
/// server normalises ZIMRA and LEKUKA responses into the same shape.
/// </summary>
public sealed class FiscalDeviceStatus
{
    public string DeviceId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string FiscalDayStatus { get; set; } = "";
    public int? FiscalDayNo { get; set; }
    public long? LastReceiptGlobalNo { get; set; }
    public string? FiscalDayClosingErrorCode { get; set; }
    public DateTimeOffset? FiscalDayOpenedAt { get; set; }
    public string? OperationId { get; set; }

    /// <summary>A close-failed day is still open on the gateway, so a retry is allowed.</summary>
    public bool IsDayOpen =>
        string.Equals(FiscalDayStatus, "FiscalDayOpened", StringComparison.OrdinalIgnoreCase) ||
        IsClosePending;

    public bool IsClosePending =>
        string.Equals(FiscalDayStatus, "FiscalDayCloseFailed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Registration card data from <c>GET /api/zimra/device-details</c> — the same
/// fields ZIMRA prints on the device card.
/// </summary>
public sealed class FiscalCardDetails
{
    public string Tin { get; set; } = "";
    public string Bpn { get; set; } = "";
    public string Vat { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string Address { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string LogoUrl { get; set; } = "";
}

public sealed record DeviceStatusResult(bool Success, string? Error, FiscalDeviceStatus? Status);

public sealed record DeviceCardResult(bool Success, string? Error, FiscalCardDetails? Card);

public sealed record FiscalDayActionResult(
    bool Success,
    bool Pending,
    string Message,
    string? ErrorCode = null,
    int? FiscalDayNo = null);
