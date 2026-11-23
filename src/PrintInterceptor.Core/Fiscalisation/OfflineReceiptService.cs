using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string ProtectedProvisioningRequired =
        "Offline signing requires the protected ZIMRA device key. Go online and use " +
        "Settings -> Refresh from FiscalStack (then restart) so the key is stored with DPAPI; " +
        "plaintext provisioning-cache keys are no longer accepted.";

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
    /// Required when offline signing is enabled. The ZIMRA private key is a
    /// credential and is only accepted from the DPAPI store; the plaintext
    /// provisioning-cache fallback has been removed.
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

        if (_secrets is null || !SecretStore.Supported)
        {
            _configurationError = ProtectedProvisioningRequired;
            _log.Warn(_configurationError);
            ScrubLegacyCacheKey();
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

    /// <summary>
    /// Signs the next offline receipt. The optional beforeCommit callback runs
    /// under the chain lock before the counter is persisted, so a caller can keep
    /// the signed receipt durably without leaving a gap in the ZIMRA sequence.
    /// </summary>
    public bool TryIssue(
        ParsedReceipt receipt,
        out OfflineSignedReceipt signed,
        out string error,
        Func<OfflineSignedReceipt, (bool Ok, string Error)>? beforeCommit = null)
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
                    TaxInclusive = _config.Parse.VatInclusive,
                    Taxes = taxes,
                    StandardTaxPercent = EffectiveStandardPercent,
                    QrBaseUrl = EffectiveQrBaseUrl
                },
                key),
            out signed,
            out error,
            beforeCommit);

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

        if (_secrets is null || !SecretStore.Supported)
        {
            _configurationError = ProtectedProvisioningRequired;
            _log.Warn(_configurationError);
            return false;
        }

        var state = await client.GetOfflineStateAsync(cancellationToken);
        if (state is null)
        {
            _configurationError ??= "Could not read the ZIMRA offline state from FiscalStack.";
            return false;
        }

        var keyPem = string.IsNullOrWhiteSpace(state.ZimraPrivateKey) ? _remoteKeyPem : state.ZimraPrivateKey;
        if (string.IsNullOrWhiteSpace(keyPem))
        {
            _configurationError = "FiscalStack returned no device private key for offline signing.";
            _log.Warn(_configurationError);
            return false;
        }

        // Protect the key first. If the store refuses the write, no state is
        // adopted and the cache never becomes the fallback key holder.
        if (!_secrets.TrySet(SecretStore.DeviceKeyEntry, keyPem, out var secretError))
        {
            _configurationError =
                "Offline provisioning could not be protected in the DPAPI secret store: " + secretError +
                ". Check the secrets folder ACL, go online and press Refresh from FiscalStack again; " +
                "plaintext caching is disabled.";
            _log.Warn(_configurationError);
            return false;
        }

        if (!ApplyState(state, fromCache: false, keyPem))
        {
            return false;
        }

        _provisionedAt = DateTimeOffset.Now;
        PersistCache(state);
        return true;
    }

    private void LoadCachedProvisioning()
    {
        if (!File.Exists(_cachePath))
        {
            _configurationError =
                "Offline provisioning has not been cached yet. Go online and use Settings -> Refresh from FiscalStack, then restart.";
            return;
        }

        // A cache written by an older version may still carry the private key in
        // clear text. It is never a key source; strip it so a support bundle of
        // the data folder stops carrying a signing credential.
        ScrubLegacyCacheKey();

        try
        {
            var cache = JsonSerializer.Deserialize<ProvisioningCache>(File.ReadAllText(_cachePath), CacheJson);
            if (cache is null)
            {
                _configurationError = "The cached offline provisioning could not be read. " +
                                      "Go online and use Settings -> Refresh from FiscalStack, then restart.";
                return;
            }

            if (!_secrets!.TryGet(SecretStore.DeviceKeyEntry, out var storedKey) ||
                string.IsNullOrWhiteSpace(storedKey))
            {
                _configurationError = ProtectedProvisioningRequired;
                _log.Warn(_configurationError);
                return;
            }

            if (!ApplyState(cache.State, fromCache: true, storedKey))
            {
                return;
            }

            _provisionedAt = cache.FetchedAt;
            _log.Info(
                $"Offline provisioning restored from cache: device={EffectiveDeviceId} " +
                $"day={cache.State.CurrentFiscalDayNo} counter={cache.State.DailyReceiptCount} " +
                $"global={cache.State.LastReceiptGlobalNo}.");
        }
        catch (Exception ex)
        {
            _configurationError = $"Could not load protected offline provisioning: {ex.Message}";
            _log.Warn(_configurationError);
        }
    }

    /// <summary>
    /// Removes a private key left in the provisioning cache by an older build.
    /// The cache is state only; the key lives in the DPAPI store.
    /// </summary>
    private void ScrubLegacyCacheKey()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_cachePath));
            if (node is not JsonObject obj)
            {
                return;
            }

            var hadCamelCase = obj.Remove("privateKeyPem");
            var hadPascalCase = obj.Remove("PrivateKeyPem");
            if (obj["state"] is JsonObject stateNode)
            {
                hadCamelCase |= stateNode.Remove("zimraPrivateKey");
                hadPascalCase |= stateNode.Remove("ZimraPrivateKey");
            }

            if (!hadCamelCase && !hadPascalCase)
            {
                return;
            }

            WriteCacheJson(node.ToJsonString());
            _log.Warn(
                "Removed a legacy plaintext ZIMRA device key from the offline provisioning cache; " +
                "it is not used for signing.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not remove a legacy device key from the offline provisioning cache: {ex.Message}");
        }
    }

    private void WriteCacheJson(string json)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            SecretStore.RestrictExistingFile(temp);
            File.Move(temp, _cachePath, overwrite: true);
            SecretStore.RestrictExistingFile(_cachePath);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // Cache holds fiscal state only; no signing key is ever written here.
            }

            throw;
        }
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

    /// <summary>
    /// Caches only device state: id, serial, tax buckets, counters and the fiscal
    /// hash. The private key never reaches this file; a store write failure is
    /// handled by RefreshStateAsync before the state is adopted.
    /// </summary>
    private void PersistCache(OfflineState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new { State = WithoutPrivateKey(state), FetchedAt = DateTimeOffset.Now }, CacheJson);
            WriteCacheJson(json);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not cache the offline provisioning: {ex.Message}");
        }
    }

    /// <summary>
    /// The API's state object includes the private key. It is already in the
    /// secret store by the time state is cached, so strip it from the copy.
    /// </summary>
    private static OfflineState WithoutPrivateKey(OfflineState state) => new()
    {
        DeviceId = state.DeviceId,
        DeviceSerialNo = state.DeviceSerialNo,
        ZimraEnvironment = state.ZimraEnvironment,
        QrUrl = state.QrUrl,
        ZimraPrivateKey = null,
        ZimraCertificate = state.ZimraCertificate,
        LastFiscalHash = state.LastFiscalHash,
        CurrentFiscalDayNo = state.CurrentFiscalDayNo,
        FiscalDayOpen = state.FiscalDayOpen,
        FiscalDayOpenedAt = state.FiscalDayOpenedAt,
        DailyReceiptCount = state.DailyReceiptCount,
        LastReceiptGlobalNo = state.LastReceiptGlobalNo,
        Taxes = state.Taxes
    };

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

        /// <summary>Legacy only: recognised so it can be scrubbed, never used as a key.</summary>
        public string? PrivateKeyPem { get; set; }

        public DateTimeOffset FetchedAt { get; set; }
    }
}
