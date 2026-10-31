using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.State;

namespace PrintInterceptor.Core.Fiscalisation;

public enum OfflineKeySource
{
    None,
    FiscalStack,
    Cache
}

/// <summary>
/// Coordinates offline signing: provisions the device key from
/// /zimra/offline-state, signs the next receipt against the durable chain, and
/// records online state so the chain stays usable across reboots.
/// </summary>
public sealed class OfflineReceiptService : IDisposable
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly InterceptorConfig _config;
    private readonly FiscalChainStore _chain;
    private readonly ILog _log;
    private readonly SecretStore? _secrets;
    private readonly string _cachePath;
    private readonly object _gate = new();

    private RSA? _remoteKey;
    private OfflineState? _remoteState;
    private List<OfflineTaxConfig>? _remoteTaxes;
    private string? _remoteKeyPem;
    private string? _configurationError;
    private DateTimeOffset? _provisionedAt;

    /// <param name="secrets">
    /// Store for the device private key. When it is unavailable the key stays in
    /// the provisioning cache as it did before, with the file ACL restricted.
    /// </param>
    public OfflineReceiptService(
        InterceptorConfig config,
        FiscalChainStore chain,
        ILog log,
        SecretStore? secrets = null)
    {
        _config = config;
        _chain = chain;
        _log = log;
        _secrets = secrets;
        _cachePath = Path.Combine(config.ResolveDataDirectory(), "state", "offline-provisioning.json");

        if (!config.Offline.Enabled)
        {
            return;
        }

        LoadCachedProvisioning();
    }

    public bool Enabled => _config.Offline.Enabled;

    public bool Ready => Enabled && ActiveKey is not null && EffectiveDeviceId.Length > 0;

    public string? ConfigurationError => _configurationError;

    public FiscalChainStore Chain => _chain;

    public OfflineKeySource KeySource
    {
        get
        {
            if (_remoteKey is null)
            {
                return OfflineKeySource.None;
            }

            return _provisionedAt is null ? OfflineKeySource.FiscalStack : OfflineKeySource.Cache;
        }
    }

    public string KeySourceText => KeySource switch
    {
        OfflineKeySource.FiscalStack => "FiscalStack API",
        OfflineKeySource.Cache => "cached from FiscalStack",
        _ => "not loaded"
    };

    public OfflineState? RemoteState => _remoteState;

    public DateTimeOffset? ProvisionedAt => _provisionedAt;

    public string EffectiveDeviceId =>
        OfflineSigner.NormalizeDeviceId(_remoteState?.DeviceId ?? "");

    public string DeviceSerialNo => _remoteState?.DeviceSerialNo ?? "";

    public IReadOnlyList<OfflineTaxConfig> EffectiveTaxes =>
        _remoteTaxes is { Count: > 0 } ? _remoteTaxes : OfflineConfig.DefaultTaxes();

    public decimal EffectiveStandardPercent
    {
        get
        {
            var live = EffectiveTaxes
                .FirstOrDefault(tax => string.Equals(tax.TaxType, "STANDARD", StringComparison.OrdinalIgnoreCase))
                ?.TaxPercent;
            if (live is > 0)
            {
                return (decimal)live.Value;
            }

            return (decimal)(_config.Parse.VatRate * 100);
        }
    }

    public string EffectiveQrBaseUrl =>
        !string.IsNullOrWhiteSpace(_remoteState?.QrUrl)
            ? _remoteState!.QrUrl
            : OfflineConfig.DefaultQrBaseUrl;

    private RSA? ActiveKey
    {
        get
        {
            lock (_gate)
            {
                return _remoteKey;
            }
        }
    }

    public bool TryIssue(ParsedReceipt receipt, out OfflineSignedReceipt signed, out string error)
    {
        signed = null!;
        if (!Enabled)
        {
            error = "Offline signing is disabled.";
            return false;
        }

        var key = ActiveKey;
        if (key is null)
        {
            error = _configurationError ?? "The offline private key is not loaded.";
            return false;
        }

        var deviceId = EffectiveDeviceId;
        if (deviceId == "0")
        {
            error = "No ZIMRA device ID is available yet. Go online once so it can be provisioned.";
            return false;
        }

        var receiptDate = OfflineSigner.FormatReceiptDate(DateTimeOffset.Now);
        var taxes = EffectiveTaxes;

        var issued = _chain.TrySignNext(
            deviceId,
            snapshot => OfflineSigner.Sign(
                new OfflineSignRequest
                {
                    DeviceId = deviceId,
                    ReceiptType = receipt.TransactionType,
                    Currency = receipt.Currency,
                    ReceiptDate = receiptDate,
                    ReceiptCounter = snapshot.ReceiptCounter + 1,
                    ReceiptGlobalNo = snapshot.ReceiptGlobalNo + 1,
                    FiscalDayNo = snapshot.FiscalDayNo,
                    PreviousHash = snapshot.LastHash,
                    Items = receipt.Items,
                    TaxInclusive = string.Equals(_config.Parse.VatFlag.Trim(), "1", StringComparison.Ordinal),
                    Taxes = taxes,
                    StandardTaxPercent = EffectiveStandardPercent,
                    QrBaseUrl = EffectiveQrBaseUrl
                },
                key),
            out signed,
            out error);

        if (!issued)
        {
            return false;
        }

        _log.Info(
            $"Offline receipt signed: global={signed.ReceiptGlobalNo} counter={signed.ReceiptCounter} " +
            $"day={signed.FiscalDayNo} total={signed.ReceiptTotal:0.00} {signed.Currency} " +
            $"hash={Short(signed.Hash)}.");
        return true;
    }

    public FiscalizeRequest PrepareSyncRequest(FiscalizeRequest request, OfflineSignedReceipt signed)
    {
        request.OfflineSignature = signed.Signature;
        request.OfflineReceiptCounter = signed.ReceiptCounter;
        request.OfflineGlobalReceiptCounter = signed.ReceiptGlobalNo;
        request.OfflinePreviousHash = signed.PreviousHash;
        request.OfflineFiscalDay = signed.FiscalDayNo;
        request.OfflineDate = signed.ReceiptDate;
        return request;
    }

    public void RecordOnlineSuccess(FiscalizeResponse response)
    {
        // Keep the chain warm even while offline signing is disabled, so it can
        // be turned on later without waiting for another online receipt.
        var deviceId = EffectiveDeviceId;
        if (deviceId == "0")
        {
            return;
        }

        _chain.RecordOnlineSuccess(deviceId, response);
    }

    /// <summary>
    /// Pulls the device id, private key, live tax buckets, QR URL and counters
    /// from FiscalStack. The API key identifies the tenant, so /api/zimra is
    /// used and no company id is needed.
    /// </summary>
    public async Task<bool> RefreshStateAsync(FiscalStackClient client, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return false;
        }

        var state = await client.GetOfflineStateAsync(cancellationToken);
        if (state is null)
        {
            _configurationError ??= "Could not read the ZIMRA offline state from FiscalStack.";
            return false;
        }

        if (!ApplyState(state, fromCache: false, string.IsNullOrWhiteSpace(state.ZimraPrivateKey) ? _remoteKeyPem : state.ZimraPrivateKey))
        {
            return false;
        }

        _provisionedAt = DateTimeOffset.Now;
        PersistCache(state, _remoteKeyPem);
        return true;
    }

    private void LoadCachedProvisioning()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<ProvisioningCache>(File.ReadAllText(_cachePath), CacheJson);
            if (cache is null)
            {
                return;
            }

            // The key belongs in the secret store; a value still present in the
            // cache was written by an older version and is migrated below.
            var storedKey = _secrets is not null && _secrets.TryGet(SecretStore.DeviceKeyEntry, out var value)
                ? value
                : null;
            var keyPem = storedKey ?? cache.PrivateKeyPem;
            if (string.IsNullOrWhiteSpace(keyPem))
            {
                return;
            }

            if (!ApplyState(cache.State, fromCache: true, keyPem))
            {
                return;
            }

            _provisionedAt = cache.FetchedAt;
            if (storedKey is null && _secrets is not null)
            {
                MigrateCachedKey(cache.State, keyPem!);
            }

            _log.Info(
                $"Offline provisioning restored from cache: device={EffectiveDeviceId} " +
                $"day={cache.State.CurrentFiscalDayNo} counter={cache.State.DailyReceiptCount} " +
                $"global={cache.State.LastReceiptGlobalNo}.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read the cached offline provisioning: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves a device private key out of the plaintext provisioning cache into the
    /// secret store and rewrites the cache without it. The ZIMRA signing key is the
    /// credential that makes a till able to issue receipts, so it must not survive
    /// in a folder that gets copied into a support bundle.
    /// </summary>
    private void MigrateCachedKey(OfflineState state, string keyPem)
    {
        if (_secrets is null)
        {
            return;
        }

        if (!_secrets.TrySet(SecretStore.DeviceKeyEntry, keyPem, out var error))
        {
            _log.Warn($"The cached device private key could not be moved to the secret store: {error}");
            return;
        }

        PersistCache(state, keyPem);
        _log.Info(
            $"The ZIMRA device private key was moved into the secret store at {_secrets.DirectoryPath}; " +
            "the provisioning cache no longer contains it.");
    }

    private bool ApplyState(OfflineState state, bool fromCache, string? keyPem)
    {
        var deviceId = OfflineSigner.NormalizeDeviceId(state.DeviceId ?? "");
        if (deviceId == "0")
        {
            _log.Warn("FiscalStack returned no ZIMRA device id; offline provisioning was not applied.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(keyPem))
        {
            try
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(keyPem);
                lock (_gate)
                {
                    _remoteKey?.Dispose();
                    _remoteKey = rsa;
                    _remoteKeyPem = keyPem;
                    _configurationError = null;
                }
            }
            catch (Exception ex)
            {
                _configurationError = $"Could not import the FiscalStack device key: {ex.Message}";
                _log.Error("Could not import the FiscalStack device key", ex);
                return false;
            }
        }
        else if (!fromCache && ActiveKey is null)
        {
            _configurationError = "FiscalStack returned no device private key for offline signing.";
            _log.Warn(_configurationError);
            return false;
        }

        _remoteState = state;
        _remoteTaxes = MapTaxes(state.Taxes);
        _chain.SeedFromServer(deviceId, state);

        _log.Info(
            $"Offline state from FiscalStack: device={deviceId} serial={state.DeviceSerialNo} " +
            $"day={state.CurrentFiscalDayNo} counter={state.DailyReceiptCount} " +
            $"global={state.LastReceiptGlobalNo} taxes={DescribeTaxes(_remoteTaxes)}.");
        return true;
    }

    private void PersistCache(OfflineState state, string? keyPem)
    {
        if (string.IsNullOrWhiteSpace(keyPem))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Only the non-secret half is kept here: device id, fiscal day and
            // counters. The key goes to the store when one is available.
            var storedInSecrets = false;
            if (_secrets is not null)
            {
                storedInSecrets = _secrets.TrySet(SecretStore.DeviceKeyEntry, keyPem, out var secretError);
                if (!storedInSecrets)
                {
                    _log.Warn(
                        "The device private key could not be stored securely and will be cached in the file: " +
                        secretError);
                }
            }

            var json = JsonSerializer.Serialize(
                new ProvisioningCache
                {
                    State = state,
                    PrivateKeyPem = storedInSecrets ? null : keyPem,
                    FetchedAt = DateTimeOffset.Now
                },
                CacheJson);

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            SecretStore.RestrictExistingFile(temp);
            File.Move(temp, _cachePath, overwrite: true);
            SecretStore.RestrictExistingFile(_cachePath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not cache the offline provisioning: {ex.Message}");
        }
    }

    private static List<OfflineTaxConfig> MapTaxes(OfflineStateTaxes? taxes)
    {
        var mapped = new List<OfflineTaxConfig>();
        Add("STANDARD", taxes?.Standard);
        Add("ZERO_RATED", taxes?.ZeroRated);
        Add("EXEMPT", taxes?.Exempt);

        // Keep the required buckets even when ZIMRA only returned a subset.
        foreach (var fallback in OfflineConfig.DefaultTaxes())
        {
            if (mapped.All(tax => !string.Equals(tax.TaxType, fallback.TaxType, StringComparison.OrdinalIgnoreCase)))
            {
                mapped.Add(fallback);
            }
        }

        return mapped;

        void Add(string taxType, OfflineStateTax? tax)
        {
            if (tax is null || tax.TaxId <= 0)
            {
                return;
            }

            mapped.Add(new OfflineTaxConfig
            {
                TaxType = taxType,
                TaxId = tax.TaxId,
                TaxCode = string.IsNullOrWhiteSpace(tax.TaxCode) ? DefaultCode(tax.TaxId) : tax.TaxCode,
                TaxPercent = tax.TaxPercent
            });
        }
    }

    private static string DefaultCode(int taxId) => taxId switch
    {
        1 => "C",
        2 => "B",
        4 => "E",
        _ => "A"
    };

    private static string DescribeTaxes(IReadOnlyList<OfflineTaxConfig>? taxes) =>
        taxes is null
            ? "none"
            : string.Join(", ", taxes.Select(tax =>
                $"{tax.TaxType}={tax.TaxCode}/{tax.TaxId}" +
                (tax.TaxPercent is { } percent ? $"@{percent:0.##}%" : "")));

    private static string Short(string value) => value[..Math.Min(12, value.Length)];

    public void Dispose()
    {
        _remoteKey?.Dispose();
    }

    private sealed class ProvisioningCache
    {
        public OfflineState State { get; set; } = new();
        public string? PrivateKeyPem { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }
}
