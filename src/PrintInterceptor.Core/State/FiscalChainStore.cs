using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;

namespace PrintInterceptor.Core.State;

public sealed class FiscalChainSnapshot
{
    public string DeviceId { get; set; } = "";
    public int FiscalDayNo { get; set; }
    public int ReceiptCounter { get; set; }
    public int ReceiptGlobalNo { get; set; }
    public string LastHash { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Durable ZIMRA counter/hash chain used for offline signing. Advancing the
/// chain is atomic with signing so two receipts can never claim the same
/// global number, and a crash after signing still leaves a record of it.
/// </summary>
public sealed class FiscalChainStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILog _log;
    private FiscalChainSnapshot? _current;

    public FiscalChainStore(string stateDirectory, ILog log)
    {
        _log = log;
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "fiscal-chain.json");
        _current = Load();
    }

    public FiscalChainSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is null ? null : Clone(_current);
            }
        }
    }

    public bool TryGetCurrent(string deviceId, out FiscalChainSnapshot snapshot, out string error)
    {
        lock (_gate)
        {
            return TryGetCurrentLocked(deviceId, out snapshot, out error);
        }
    }

    /// <summary>
    /// Atomically signs the next receipt and advances the chain. The callback
    /// runs while the store is locked so counters cannot be claimed twice.
    /// </summary>
    public bool TrySignNext(
        string deviceId,
        Func<FiscalChainSnapshot, OfflineSignedReceipt> signer,
        out OfflineSignedReceipt signed,
        out string error)
    {
        lock (_gate)
        {
            signed = null!;
            if (TryGetCurrentLocked(deviceId, out var snapshot, out error) is false)
            {
                return false;
            }

            OfflineSignedReceipt receipt;
            try
            {
                receipt = signer(snapshot);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var next = new FiscalChainSnapshot
            {
                DeviceId = deviceId,
                FiscalDayNo = receipt.FiscalDayNo,
                ReceiptCounter = receipt.ReceiptCounter,
                ReceiptGlobalNo = receipt.ReceiptGlobalNo,
                LastHash = receipt.Hash,
                UpdatedAt = DateTimeOffset.Now
            };

            try
            {
                SaveLocked(next);
            }
            catch (Exception ex)
            {
                _log.Error("Could not persist the offline fiscal chain; the receipt was NOT issued", ex);
                error = $"Could not persist the fiscal chain before printing: {ex.Message}";
                return false;
            }

            _current = next;
            signed = receipt;
            error = "";
            return true;
        }
    }

    /// <summary>
    /// Records the authoritative counters and hash from a successful online
    /// fiscalisation. Older responses never rewind a chain that is already ahead.
    /// </summary>
    public void RecordOnlineSuccess(string deviceId, FiscalizeResponse response)
    {
        var receipt = response.Receipt;
        var globalNo = receipt?.ReceiptGlobalNo ?? (int?)response.ReceiptNumber;
        var counter = receipt?.ReceiptCounter;
        var day = receipt?.FiscalDayNo;
        var hash = response.FiscalCode;

        if (globalNo is null && counter is null && day is null && string.IsNullOrWhiteSpace(hash))
        {
            _log.Warn("Online fiscalisation returned no counters or hash; offline chain was not advanced.");
            return;
        }

        lock (_gate)
        {
            var current = _current;
            if (current is not null && !string.IsNullOrWhiteSpace(current.DeviceId) &&
                !string.Equals(current.DeviceId, deviceId, StringComparison.Ordinal))
            {
                _log.Warn(
                    $"Ignoring online fiscal state for device {deviceId}: offline chain belongs to {current.DeviceId}.");
                return;
            }

            if (current is not null && globalNo is not null && globalNo < current.ReceiptGlobalNo)
            {
                _log.Warn(
                    $"Online receipt global {globalNo} is behind the offline chain ({current.ReceiptGlobalNo}); " +
                    "keeping the newer chain.");
                return;
            }

            var next = new FiscalChainSnapshot
            {
                DeviceId = deviceId,
                FiscalDayNo = day ?? current?.FiscalDayNo ?? 0,
                ReceiptCounter = counter ?? current?.ReceiptCounter ?? 0,
                ReceiptGlobalNo = globalNo ?? current?.ReceiptGlobalNo ?? 0,
                LastHash = string.IsNullOrWhiteSpace(hash) ? current?.LastHash ?? "" : hash,
                UpdatedAt = DateTimeOffset.Now
            };

            try
            {
                SaveLocked(next);
                _current = next;
            }
            catch (Exception ex)
            {
                _log.Error("Could not persist the fiscal chain after an online receipt", ex);
            }
        }
    }

    /// <summary>
    /// Seeds the chain from /api/companies/:id/zimra/offline-state. Used only at
    /// startup, before any receipt of this run has been signed.
    /// </summary>
    public void SeedFromServer(string deviceId, OfflineState state)
    {
        lock (_gate)
        {
            var next = new FiscalChainSnapshot
            {
                DeviceId = deviceId,
                FiscalDayNo = state.CurrentFiscalDayNo,
                ReceiptCounter = state.DailyReceiptCount,
                ReceiptGlobalNo = state.LastReceiptGlobalNo,
                LastHash = state.LastFiscalHash ?? "",
                UpdatedAt = DateTimeOffset.Now
            };

            if (_current is not null &&
                _current.ReceiptGlobalNo > next.ReceiptGlobalNo)
            {
                _log.Warn(
                    $"Server offline-state global {next.ReceiptGlobalNo} is behind the local chain " +
                    $"({_current.ReceiptGlobalNo}); keeping local state.");
                return;
            }

            try
            {
                SaveLocked(next);
                _current = next;
                _log.Info(
                    $"Fiscal chain seeded from server: day={next.FiscalDayNo} counter={next.ReceiptCounter} " +
                    $"global={next.ReceiptGlobalNo} hash={(next.LastHash.Length > 12 ? next.LastHash[..12] : next.LastHash)}.");
            }
            catch (Exception ex)
            {
                _log.Error("Could not persist the fiscal chain seeded from the server", ex);
            }
        }
    }

    private bool TryGetCurrentLocked(string deviceId, out FiscalChainSnapshot snapshot, out string error)
    {
        if (_current is null)
        {
            snapshot = new FiscalChainSnapshot();
            error = "No fiscal chain state yet. Go online once so the first receipt seeds the counters, " +
                    "or enable offline provisioning to bootstrap from the server.";
            return false;
        }

        if (!string.Equals(_current.DeviceId, deviceId, StringComparison.Ordinal))
        {
            snapshot = new FiscalChainSnapshot();
            error = $"Fiscal chain belongs to device {_current.DeviceId}, but FiscalStack provisioned {deviceId}. " +
                    "Check that the API key still points at the same company/device, or delete the chain deliberately.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_current.LastHash) ||
            _current.ReceiptGlobalNo <= 0 ||
            _current.ReceiptCounter <= 0 ||
            _current.FiscalDayNo <= 0)
        {
            snapshot = new FiscalChainSnapshot();
            error = "The fiscal chain has no valid previous hash/counters. Sign the first receipt online once.";
            return false;
        }

        snapshot = Clone(_current);
        error = "";
        return true;
    }

    private FiscalChainSnapshot? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var snapshot = JsonSerializer.Deserialize<FiscalChainSnapshot>(json, JsonOptions);
            if (snapshot is not null)
            {
                _log.Info(
                    $"Fiscal chain loaded: device={snapshot.DeviceId} day={snapshot.FiscalDayNo} " +
                    $"counter={snapshot.ReceiptCounter} global={snapshot.ReceiptGlobalNo}.");
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            // An unreadable chain must not silently reset to zero: that would
            // let the till sign receipts with duplicate global numbers.
            throw new InvalidOperationException(
                $"Could not read the fiscal chain at {_path}. Restore or remove it deliberately before starting.", ex);
        }
    }

    private void SaveLocked(FiscalChainSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var temp = _path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static FiscalChainSnapshot Clone(FiscalChainSnapshot snapshot) => new()
    {
        DeviceId = snapshot.DeviceId,
        FiscalDayNo = snapshot.FiscalDayNo,
        ReceiptCounter = snapshot.ReceiptCounter,
        ReceiptGlobalNo = snapshot.ReceiptGlobalNo,
        LastHash = snapshot.LastHash,
        UpdatedAt = snapshot.UpdatedAt
    };
}

public sealed class OfflineState
{
    public string DeviceId { get; set; } = "";
    public string DeviceSerialNo { get; set; } = "";
    public string ZimraEnvironment { get; set; } = "";
    public string QrUrl { get; set; } = "";
    public string? ZimraPrivateKey { get; set; }
    public string? ZimraCertificate { get; set; }
    public string? LastFiscalHash { get; set; }
    public int CurrentFiscalDayNo { get; set; }
    public bool FiscalDayOpen { get; set; }
    public DateTimeOffset? FiscalDayOpenedAt { get; set; }
    public int DailyReceiptCount { get; set; }
    public int LastReceiptGlobalNo { get; set; }
    public OfflineStateTaxes? Taxes { get; set; }
}

public sealed class OfflineStateTaxes
{
    [JsonPropertyName("STANDARD")]
    public OfflineStateTax? Standard { get; set; }

    [JsonPropertyName("ZERO_RATED")]
    public OfflineStateTax? ZeroRated { get; set; }

    [JsonPropertyName("EXEMPT")]
    public OfflineStateTax? Exempt { get; set; }
}

public sealed class OfflineStateTax
{
    public int TaxId { get; set; }
    public string TaxCode { get; set; } = "";
    public double TaxPercent { get; set; }
}
