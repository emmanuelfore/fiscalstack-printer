using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintInterceptor.Core.Configuration;

public sealed class InterceptorConfig
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 9100;
    public int ReadIdleTimeoutSeconds { get; set; } = 3;
    public long MaxJobBytes { get; set; } = 10 * 1024 * 1024;

    public string ApiEndpoint { get; set; } = "https://fiscalstack.co.zw/api/v1/";

    /// <summary>
    /// Runtime API key. It is read from the secret store, the environment or an
    /// existing config file at startup, but <see cref="Save"/> never writes it
    /// back: the secret lives in <see cref="SecretStore"/> instead.
    /// </summary>
    public string ApiKey { get; set; } = "";
    public int ApiTimeoutSeconds { get; set; } = 30;

    /// <summary>Full path to gswin64c.exe / gs. Empty = search PATH and standard install folders.</summary>
    public string GhostscriptPath { get; set; } = "";
    public int GhostscriptTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Existing configurations default to completed so an upgrade does not force
    /// a setup dialog. New installations set this false and the tray opens Settings
    /// until the operator saves the initial configuration.
    /// </summary>
    public bool SetupCompleted { get; set; } = true;

    public CompanyConfig Company { get; set; } = new();
    public PrintingConfig Printing { get; set; } = new();
    public ParseConfig Parse { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public OfflineConfig Offline { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    private readonly object _saveGate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FiscalStack", "PrintInterceptor", "config.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "fiscalstack", "printinterceptor", "config.json");
    }

    public static InterceptorConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var created = new InterceptorConfig { ConfigPath = path, SetupCompleted = false };
            created.Save(path);
            return created;
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<InterceptorConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException($"Could not read configuration at {path}");
        config.ConfigPath = path;
        config.ApplyDefaults();
        return config;
    }

    public string ResolveSecretDirectory()
    {
        return SecretStore.DefaultDirectory(string.IsNullOrWhiteSpace(ConfigPath) ? null : ConfigPath);
    }

    public void Save(string? path = null)
    {
        var target = path ?? ConfigPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("No configuration path supplied.");
        }

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Serialise without the secret, then restore it in memory. A save must not
        // be able to put the key on disk, whatever the caller assigned to it.
        var persisted = ApiKey;
        string json;
        lock (_saveGate)
        {
            ApiKey = "";
            try
            {
                json = JsonSerializer.Serialize(this, JsonOptions);
            }
            finally
            {
                ApiKey = persisted;
            }
        }

        File.WriteAllText(target, json);
    }

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Storage.DataDirectory))
        {
            return Storage.DataDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FiscalStack", "PrintInterceptor", "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "fiscalstack", "printinterceptor");
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!System.Net.IPAddress.TryParse(ListenAddress, out _))
        {
            errors.Add($"listenAddress '{ListenAddress}' is not an IP address.");
        }

        if (ListenPort is < 1 or > 65535)
        {
            errors.Add($"listenPort {ListenPort} is outside 1-65535.");
        }

        if (!Uri.TryCreate(ApiEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"apiEndpoint '{ApiEndpoint}' must be an absolute http(s) URL.");
        }

        if (ReadIdleTimeoutSeconds is < 1 or > 120)
        {
            errors.Add("readIdleTimeoutSeconds must be between 1 and 120.");
        }

        if (MaxJobBytes is < 1024 or > 100 * 1024 * 1024)
        {
            errors.Add("maxJobBytes must be between 1 KiB and 100 MiB.");
        }

        if (Printing.PaperWidthMm is < 40 or > 120)
        {
            errors.Add("printing.paperWidthMm must be between 40 and 120.");
        }

        if (Printing.Format is not ("thermal" or "a4" or "auto"))
        {
            errors.Add("printing.format must be 'thermal', 'a4' or 'auto'.");
        }

        if (Printing.Copies is < 1 or > 5)
        {
            errors.Add("printing.copies must be between 1 and 5.");
        }

        if (Parse.VatRate is < 0 or > 1)
        {
            errors.Add("parse.vatRate must be a fraction between 0 and 1.");
        }

        if (Offline.Enabled && string.IsNullOrWhiteSpace(ApiKey))
        {
            errors.Add("offline.enabled requires apiKey: the device, key, taxes and counters are provisioned from FiscalStack.");
        }

        return errors;
    }

    private void ApplyDefaults()
    {
        Company ??= new CompanyConfig();
        Printing ??= new PrintingConfig();
        Parse ??= new ParseConfig();
        Storage ??= new StorageConfig();
        Offline ??= new OfflineConfig();

        Parse.Currencies ??= new List<CurrencyKeyword>();
        if (Parse.Currencies.Count == 0)
        {
            Parse.Currencies = ParseConfig.DefaultCurrencies();
        }

        Parse.VatFlag = string.IsNullOrWhiteSpace(Parse.VatFlag) ? "1" : Parse.VatFlag.Trim();
        Printing.Format = string.IsNullOrWhiteSpace(Printing.Format)
            ? "thermal"
            : Printing.Format.Trim().ToLowerInvariant();
        ApiEndpoint = string.IsNullOrWhiteSpace(ApiEndpoint) ? "https://fiscalstack.co.zw/api/v1/" : ApiEndpoint.Trim();
        if (!ApiEndpoint.EndsWith('/'))
        {
            ApiEndpoint += "/";
        }
    }
}

public sealed class CompanyConfig
{
    public string Name { get; set; } = "";
    public string Tin { get; set; } = "";
    public string VatNumber { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>Optional logo shown on A4 invoices. PNG/JPG; relative to the config file.</summary>
    public string LogoPath { get; set; } = "";
}

public sealed class PrintingConfig
{
    public bool Enabled { get; set; } = true;
    public string PrinterName { get; set; } = "";
    public int PaperWidthMm { get; set; } = 80;
    public int Copies { get; set; } = 1;
    public int RetryCount { get; set; } = 2;
    public string AccentColor { get; set; } = "#2563EB";
    public string FooterText { get; set; } = "Thank you for your business!";

    /// <summary>
    /// thermal = 58/80 mm fiscal receipt; a4 = full-page fiscal invoice; auto =
    /// A4 when the captured print job was a PDF, thermal otherwise.
    /// </summary>
    public string Format { get; set; } = "thermal";

    /// <summary>A4 invoice printer. Empty = printerName, then the Windows default.</summary>
    public string A4PrinterName { get; set; } = "";
}

public sealed class StorageConfig
{
    public string DataDirectory { get; set; } = "";
    public bool RetainJobs { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
}

public sealed class ParseConfig
{
    public string ProductStartLine { get; set; } = "Order No.";
    public string ProductEndLine { get; set; } = "Items count";
    public string InvoiceNumber { get; set; } = "Order No.";
    public string CustomerName { get; set; } = "Buyer Name";
    public string CustomerAddress { get; set; } = "Buyer Address";
    public string CustomerEmail { get; set; } = "Buyer Email";
    public string CustomerTIN { get; set; } = "Buyer TIN";
    public string CustomerVATNumber { get; set; } = "Buyer VAT";
    public string CustomerTelephoneNumber { get; set; } = "Buyer Phone";
    public string Currency { get; set; } = "USD";
    public string PaymentMethod { get; set; } = "CASH";
    public List<CurrencyKeyword> Currencies { get; set; } = DefaultCurrencies();

    /// <summary>"1" = printed item prices include VAT, "0" = prices exclude VAT.</summary>
    public string VatFlag { get; set; } = "1";
    public double VatRate { get; set; } = 0.15;

    /// <summary>Marks a line as VAT-bearing when present in the item name. Empty = all standard.</summary>
    public string TaxSymbol { get; set; } = "";

    /// <summary>Marks a line as exempt when present in the item name.</summary>
    public string NonTaxSymbol { get; set; } = "";

    public static List<CurrencyKeyword> DefaultCurrencies() => new()
    {
        new CurrencyKeyword { Keyword = "USD", Name = "USD" },
        new CurrencyKeyword { Keyword = "US$", Name = "USD" },
        new CurrencyKeyword { Keyword = "ZWG", Name = "ZWG" },
        new CurrencyKeyword { Keyword = "ZWL", Name = "ZWG" },
        new CurrencyKeyword { Keyword = "LSL", Name = "LSL" }
    };
}

public sealed class CurrencyKeyword
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class OfflineConfig
{
    public const string DefaultQrBaseUrl = "https://fdms.zimra.co.zw/";

    /// <summary>
    /// Sign receipts locally while the FiscalStack API is unreachable. The
    /// device id, private key, tax buckets and counters are provisioned from
    /// /api/zimra/offline-state with the API key and cached for restarts.
    /// </summary>
    public bool Enabled { get; set; }

    public static List<OfflineTaxConfig> DefaultTaxes() => new()
    {
        new OfflineTaxConfig { TaxType = "STANDARD", TaxId = 3, TaxCode = "A" },
        new OfflineTaxConfig { TaxType = "ZERO_RATED", TaxId = 2, TaxCode = "B" },
        new OfflineTaxConfig { TaxType = "EXEMPT", TaxId = 1, TaxCode = "C" }
    };
}

public sealed class OfflineTaxConfig
{
    public string TaxType { get; set; } = "";
    public int TaxId { get; set; }
    public string TaxCode { get; set; } = "";

    /// <summary>Live percent from the ZIMRA tax table; null = use the configured percent.</summary>
    public double? TaxPercent { get; set; }
}
