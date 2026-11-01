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

        if (Printing.Template is not ("fiscal" or "original"))
        {
            errors.Add("printing.template must be 'fiscal' or 'original'.");
        }

        if (Printing.Template == "original" && !Storage.RetainJobs)
        {
            errors.Add("printing.template 'original' stamps the POS's own job, so storage.retainJobs must keep it.");
        }

        if (Printing.Copies is < 1 or > 5)
        {
            errors.Add("printing.copies must be between 1 and 5.");
        }

        if (Parse.VatRate is < 0 or > 1)
        {
            errors.Add("parse.vatRate must be a fraction between 0 and 1.");
        }

        foreach (var parseError in Parse.ValidateRules())
        {
            errors.Add(parseError);
        }

        if (Offline.Enabled && string.IsNullOrWhiteSpace(ApiKey))
        {
            errors.Add("offline.enabled requires apiKey: the device, key, taxes and counters are provisioned from FiscalStack.");
        }

        return errors;
    }

    public static bool IsProfileId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 40 &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') &&
        char.IsAsciiLetterOrDigit(id[0]);

    private void ApplyDefaults()
    {
        Company ??= new CompanyConfig();
        Printing ??= new PrintingConfig();
        Parse ??= new ParseConfig();
        Storage ??= new StorageConfig();
        Offline ??= new OfflineConfig();

        Parse.Patterns ??= ParsePatterns.Defaults();
        Parse.Limits ??= ParseLimits.Defaults();
        Parse.DateLabels ??= ParseConfig.DefaultDateLabels();
        Parse.DateFormats ??= ParseConfig.DefaultDateFormats();
        Parse.VatNumberLabels ??= ParseConfig.DefaultVatNumberLabels();
        Parse.TinLabels ??= ParseConfig.DefaultTinLabels();
        Parse.PhoneLabels ??= ParseConfig.DefaultPhoneLabels();
        Parse.AddressLabels ??= ParseConfig.DefaultAddressLabels();
        Parse.BuyerNameLiterals ??= ParseConfig.DefaultBuyerNameLiterals();
        Parse.InvoiceNumberKeywords ??= ParseConfig.DefaultInvoiceNumberKeywords();
        Parse.BuyerNameKeywords ??= ParseConfig.DefaultBuyerNameKeywords();
        Parse.NonItemKeywords ??= ParseConfig.DefaultNonItemKeywords();
        Parse.ItemTableHeaderRequires ??= ParseConfig.DefaultItemTableHeaderRequires();
        Parse.ItemTableHeaderAnyOf ??= ParseConfig.DefaultItemTableHeaderAnyOf();
        Parse.ItemTableEndMarkers ??= ParseConfig.DefaultItemTableEndMarkers();
        Parse.TaxRules ??= ParseConfig.DefaultTaxRules();
        Parse.NormalizeTaxRules();

        if (string.IsNullOrWhiteSpace(Parse.Profile))
        {
            Parse.Profile = "sage";
        }

        Parse.Currencies ??= new List<CurrencyKeyword>();
        if (Parse.Currencies.Count == 0)
        {
            Parse.Currencies = ParseConfig.DefaultCurrencies();
        }

        Parse.VatFlag = string.IsNullOrWhiteSpace(Parse.VatFlag) ? "1" : Parse.VatFlag.Trim();
        Printing.Format = string.IsNullOrWhiteSpace(Printing.Format)
            ? "thermal"
            : Printing.Format.Trim().ToLowerInvariant();
        Printing.Template = string.IsNullOrWhiteSpace(Printing.Template)
            ? "fiscal"
            : Printing.Template.Trim().ToLowerInvariant();
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
    public string AccentColor { get; set; } = "#4F46E5";
    public string FooterText { get; set; } = "Thank you for your business!";

    /// <summary>
    /// thermal = 58/80 mm fiscal receipt; a4 = full-page fiscal invoice; auto =
    /// A4 when the captured print job was a PDF, thermal otherwise.
    /// </summary>
    public string Format { get; set; } = "thermal";

    /// <summary>
    /// fiscal = this app renders the document (default); original = print the POS's
    /// own job and stamp the fiscal proof onto it, so the customer copy keeps the
    /// layout the shop is used to. See printing.format for the target layout.
    /// </summary>
    public string Template { get; set; } = "fiscal";

    /// <summary>A4 invoice printer. Empty = printerName, then the Windows default.</summary>
    public string A4PrinterName { get; set; } = "";
}

public sealed class StorageConfig
{
    public string DataDirectory { get; set; } = "";
    public bool RetainJobs { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// The receipt-layout rule set. It is the resolved half of a parse profile: a profile
/// file names these rules, and picking the profile writes them here, so the config is
/// always self-sufficient even if the profile file is later removed.
/// Everything in here describes *where text is found* and *how a line declares its VAT
/// status*. What must add up — qty × price, the printed total, the column scoring — stays
/// in the parser and cannot be configured, so an imported profile can widen support
/// without weakening fiscal correctness.
/// </summary>
public sealed class ParseConfig
{
    /// <summary>Provenance only: the profile whose rules are currently resolved here.</summary>
    public string Profile { get; set; } = "sage";

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

    /// <summary>
    /// The bucket given to an item line that carries no tax marker anywhere. This chooses
    /// the ZIMRA tax bucket only; the rate the gateway charges comes from the company
    /// profile behind the API key, never from here.
    /// </summary>
    public string DefaultTaxType { get; set; } = TaxTypeStandard;

    /// <summary>
    /// How a printed line declares its VAT status, highest precedence first: the first
    /// rule with a marker on the line wins. A line claimed by two different buckets keeps
    /// the earlier rule and carries a warning - a sale is never held for tax. Markers are
    /// read only where tax is declared, never inside the product description, so an item
    /// called "Zero Sugar Coke" stays taxable.
    /// </summary>
    public List<TaxMarkerRule> TaxRules { get; set; } = DefaultTaxRules();

    /// <summary>Labels that introduce the document date, e.g. "Date", "Dated".</summary>
    public List<string> DateLabels { get; set; } = DefaultDateLabels();

    /// <summary>Accepted date formats, tried in order.</summary>
    public List<string> DateFormats { get; set; } = DefaultDateFormats();

    /// <summary>Fallback labels for the buyer's VAT number, after customerVatNumber.</summary>
    public List<string> VatNumberLabels { get; set; } = DefaultVatNumberLabels();

    /// <summary>Fallback labels for the buyer's TIN, after customerTIN.</summary>
    public List<string> TinLabels { get; set; } = DefaultTinLabels();

    /// <summary>Fallback labels for the buyer's phone, after customerTelephoneNumber.</summary>
    public List<string> PhoneLabels { get; set; } = DefaultPhoneLabels();

    /// <summary>Fallback labels for the buyer's address, after customerAddress.</summary>
    public List<string> AddressLabels { get; set; } = DefaultAddressLabels();

    /// <summary>Whole lines that name a walk-in buyer, matched case-insensitively.</summary>
    public List<string> BuyerNameLiterals { get; set; } = DefaultBuyerNameLiterals();

    /// <summary>Loose keywords used to find the document number when the label misses.</summary>
    public List<string> InvoiceNumberKeywords { get; set; } = DefaultInvoiceNumberKeywords();

    /// <summary>Loose keywords used to find the buyer when the label misses.</summary>
    public List<string> BuyerNameKeywords { get; set; } = DefaultBuyerNameKeywords();

    /// <summary>A line containing any of these (lower-case, substring) is never an item.</summary>
    public List<string> NonItemKeywords { get; set; } = DefaultNonItemKeywords();

    /// <summary>
    /// A collapsed invoice-table header must contain all of these, with every space and
    /// punctuation mark removed and the line upper-cased, e.g.
    /// "DescriptionQuantityExcl. Price…Excl. Total" collapses to "DESCRIPTIONQUANTITY…EXCLTOTAL".
    /// </summary>
    public List<string> ItemTableHeaderRequires { get; set; } = DefaultItemTableHeaderRequires();

    /// <summary>…and at least one of these, which is how the total columns are labelled.</summary>
    public List<string> ItemTableHeaderAnyOf { get; set; } = DefaultItemTableHeaderAnyOf();

    /// <summary>The item table ends at a line starting with any of these (lower-case prefix).</summary>
    public List<string> ItemTableEndMarkers { get; set; } = DefaultItemTableEndMarkers();

    public ParsePatterns Patterns { get; set; } = ParsePatterns.Defaults();
    public ParseLimits Limits { get; set; } = ParseLimits.Defaults();

    /// <summary>
    /// Checks the layout rules on their own, so an imported profile is validated before
    /// it can reach a running configuration. Every message is prefixed "parse." because
    /// these are the same errors <see cref="InterceptorConfig.Validate"/> reports.
    /// </summary>
    public IReadOnlyList<string> ValidateRules()
    {
        var errors = new List<string>();

        foreach (var patternError in Patterns.Validate())
        {
            errors.Add(patternError);
        }

        if (Limits.LineTolerance is <= 0 or > 100)
        {
            errors.Add("parse.limits.lineTolerance must be between 0 and 100 currency units.");
        }

        if (Limits.RelativeTolerance is < 0 or > 0.5m)
        {
            errors.Add("parse.limits.relativeTolerance must be a fraction between 0 and 0.5.");
        }

        if (Limits.MinConfidence is < 0 or > 1)
        {
            errors.Add("parse.limits.minConfidence must be a fraction between 0 and 1.");
        }

        if (Limits.MinItemTokens is < 3 or > 12)
        {
            errors.Add("parse.limits.minItemTokens must be between 3 and 12.");
        }

        if (Limits.MaxInvoiceNumberLength is < 1 or > 100 ||
            Limits.MaxBuyerNameLength is < 10 or > 200 ||
            Limits.MinIdentifierLength is < 1 or > 40)
        {
            errors.Add("parse.limits text lengths are outside their sane ranges.");
        }

        if (Limits.ValidYearFrom >= Limits.ValidYearTo)
        {
            errors.Add("parse.limits.validYearFrom must be before validYearTo.");
        }

        if (Limits.RegexTimeoutMilliseconds is < 50 or > 5000)
        {
            errors.Add("parse.limits.regexTimeoutMilliseconds must be between 50 and 5000.");
        }

        if (DateFormats.Count == 0)
        {
            errors.Add("parse.dateFormats must list at least one format.");
        }
        else if (DateFormats.Any(format => !IsDateFormat(format)))
        {
            errors.Add("parse.dateFormats entries must use yyyy, MM, dd, HH, mm or ss tokens.");
        }

        foreach (var taxError in ValidateTaxRules())
        {
            errors.Add(taxError);
        }

        if (!InterceptorConfig.IsProfileId(Profile))
        {
            errors.Add($"parse.profile '{Profile}' must be 1-40 letters, digits or dashes.");
        }

        return errors;
    }

    private IEnumerable<string> ValidateTaxRules()
    {
        if (CanonicalTaxType(DefaultTaxType) is null)
        {
            yield return $"parse.defaultTaxType '{DefaultTaxType}' must be STANDARD, ZERO_RATED or EXEMPT.";
        }

        foreach (var (field, symbol) in new[] { ("parse.taxSymbol", TaxSymbol), ("parse.nonTaxSymbol", NonTaxSymbol) })
        {
            if (!string.IsNullOrWhiteSpace(symbol) && !symbol.Any(char.IsLetter))
            {
                yield return $"{field} '{symbol}' has no letters; a figure is read as an amount, not as a tax marker.";
            }
        }

        foreach (var rule in TaxRules)
        {
            if (CanonicalTaxType(rule.TaxType) is null)
            {
                yield return $"parse.taxRules entry '{rule.TaxType}' must be STANDARD, ZERO_RATED or EXEMPT.";
            }

            if (rule.Markers is null or { Count: 0 })
            {
                yield return $"parse.taxRules entry '{rule.TaxType}' has no markers to match.";
            }
            else
            {
                // A marker of only digits - "0%", "15" - is inert: the parser reads those
                // tokens as amounts and never as a tax statement. Say so at import.
                foreach (var inert in rule.Markers.Where(marker => !string.IsNullOrWhiteSpace(marker) && !marker.Any(char.IsLetter)))
                {
                    yield return $"parse.taxRules marker '{inert}' in entry '{rule.TaxType}' has no letters; " +
                        "a figure is read as an amount, not as a tax marker.";
                }
            }

            foreach (var window in rule.Where ?? new List<string>())
            {
                if (CanonicalTaxWindow(window) is null)
                {
                    yield return $"parse.taxRules entry '{rule.TaxType}' names window '{window}', " +
                        "which is not read by the parser; expected slot, annotation or section.";
                }
            }
        }
    }

    /// <summary>
    /// Rewrites the tax vocabulary into the forms the parser matches, so a hand-written
    /// profile can spell it as "zero-rated" or "Non-VAT" and still work.
    /// </summary>
    public void NormalizeTaxRules()
    {
        DefaultTaxType = CanonicalTaxType(DefaultTaxType) ?? TaxTypeStandard;

        foreach (var rule in TaxRules)
        {
            rule.TaxType = CanonicalTaxType(rule.TaxType) ?? rule.TaxType.Trim().ToUpperInvariant();
            rule.Markers = (rule.Markers ?? new List<string>())
                .Where(marker => !string.IsNullOrWhiteSpace(marker))
                .Select(marker => marker.Trim())
                .ToList();
            rule.Where = (rule.Where ?? new List<string>())
                .Select(window => CanonicalTaxWindow(window) ?? window.Trim().ToLowerInvariant())
                .ToList();
        }
    }

    /// <summary>
    /// A complete snapshot of the layout rules, for "save the working configuration as a
    /// profile". How a line <em>declares</em> its VAT status is part of the layout, so the
    /// marker vocabulary travels with the profile. The numbers a receipt is fiscalised with
    /// — vatRate, vatFlag, currencies, payment method and the legacy name-symbol knobs — are
    /// deliberately left out: a profile must not be able to change them.
    /// </summary>
    public ParseProfileRules ExportRules() => new()
    {
        ProductStartLine = ProductStartLine,
        ProductEndLine = ProductEndLine,
        InvoiceNumber = InvoiceNumber,
        CustomerName = CustomerName,
        CustomerAddress = CustomerAddress,
        CustomerTIN = CustomerTIN,
        CustomerVATNumber = CustomerVATNumber,
        CustomerTelephoneNumber = CustomerTelephoneNumber,
        DateLabels = DateLabels,
        DateFormats = DateFormats,
        VatNumberLabels = VatNumberLabels,
        TinLabels = TinLabels,
        PhoneLabels = PhoneLabels,
        AddressLabels = AddressLabels,
        BuyerNameLiterals = BuyerNameLiterals,
        InvoiceNumberKeywords = InvoiceNumberKeywords,
        BuyerNameKeywords = BuyerNameKeywords,
        NonItemKeywords = NonItemKeywords,
        ItemTableHeaderRequires = ItemTableHeaderRequires,
        ItemTableHeaderAnyOf = ItemTableHeaderAnyOf,
        ItemTableEndMarkers = ItemTableEndMarkers,
        DefaultTaxType = DefaultTaxType,
        TaxRules = TaxRules
            .Select(rule => new TaxMarkerRule
            {
                TaxType = rule.TaxType,
                Markers = rule.Markers.ToList(),
                Where = rule.Where.ToList()
            })
            .ToList(),
        Patterns = new ParseProfilePatterns
        {
            InvoiceNumber = Patterns.InvoiceNumber,
            InvoiceRow = Patterns.InvoiceRow,
            TwoLineQuantity = Patterns.TwoLineQuantity,
            TwoLineNameAmount = Patterns.TwoLineNameAmount,
            CurrencyBeforeAmount = Patterns.CurrencyBeforeAmount,
            BuyerLabel = Patterns.BuyerLabel,
            AmountLine = Patterns.AmountLine,
            Email = Patterns.Email
        },
        Limits = new ParseProfileLimits
        {
            LineTolerance = Limits.LineTolerance,
            RelativeTolerance = Limits.RelativeTolerance,
            MinConfidence = Limits.MinConfidence,
            MinItemTokens = Limits.MinItemTokens,
            MaxInvoiceNumberLength = Limits.MaxInvoiceNumberLength,
            MaxBuyerNameLength = Limits.MaxBuyerNameLength,
            MinIdentifierLength = Limits.MinIdentifierLength,
            ValidYearFrom = Limits.ValidYearFrom,
            ValidYearTo = Limits.ValidYearTo,
            RegexTimeoutMilliseconds = Limits.RegexTimeoutMilliseconds
        }
    };

    private static bool IsDateFormat(string format) =>
        !string.IsNullOrWhiteSpace(format) &&
        (format.Contains("yyyy", StringComparison.Ordinal) ||
         format.Contains("MM", StringComparison.Ordinal) ||
         format.Contains("dd", StringComparison.Ordinal) ||
         format.Contains("HH", StringComparison.Ordinal) ||
         format.Contains("mm", StringComparison.Ordinal) ||
         format.Contains("ss", StringComparison.Ordinal));

    public static List<CurrencyKeyword> DefaultCurrencies() => new()
    {
        new CurrencyKeyword { Keyword = "USD", Name = "USD" },
        new CurrencyKeyword { Keyword = "US$", Name = "USD" },
        new CurrencyKeyword { Keyword = "ZWG", Name = "ZWG" },
        new CurrencyKeyword { Keyword = "ZWL", Name = "ZWG" },
        new CurrencyKeyword { Keyword = "LSL", Name = "LSL" }
    };

    /// <summary>The three VAT buckets the gateway accepts for a line.</summary>
    public const string TaxTypeStandard = "STANDARD";
    public const string TaxTypeZeroRated = "ZERO_RATED";
    public const string TaxTypeExempt = "EXEMPT";

    /// <summary>The token(s) after the amount column, or the invoice row's tax column.</summary>
    public const string TaxWindowSlot = "slot";

    /// <summary>Text inside (), [], {} or &lt;&gt; on the item line.</summary>
    public const string TaxWindowAnnotation = "annotation";

    /// <summary>A header line that sets the bucket of the item lines below it.</summary>
    public const string TaxWindowSection = "section";

    /// <summary>
    /// The gateway maps a tax type straight onto a ZIMRA bucket, so a config or profile
    /// that spells it loosely still has to land on one of the three names it accepts.
    /// </summary>
    public static string? CanonicalTaxType(string? value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "STANDARD" => TaxTypeStandard,
        "ZERO" or "ZERO_RATED" or "ZERO-RATED" or "ZERO RATED" or "ZERORATED" => TaxTypeZeroRated,
        "EXEMPT" or "NON-VAT" or "NON VAT" or "NONVAT" or "NO VAT" => TaxTypeExempt,
        _ => null
    };

    public static string? CanonicalTaxWindow(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        TaxWindowSlot => TaxWindowSlot,
        TaxWindowAnnotation => TaxWindowAnnotation,
        TaxWindowSection => TaxWindowSection,
        _ => null
    };

    /// <summary>
    /// A ZIMRA till prints its tax codes as A, B and C after the amount, which is what
    /// <c>offline.taxes</c> provisioned them from, so the letters come with the words.
    /// STANDARD is limited to the slot and annotation windows on purpose: a "VAT" heading is
    /// far more likely to be a totals block than a statement that every line below it is
    /// taxable, which is already the default.
    /// </summary>
    public static List<TaxMarkerRule> DefaultTaxRules() => new()
    {
        new TaxMarkerRule
        {
            TaxType = TaxTypeExempt,
            Markers = new List<string> { "EXEMPT", "EX", "NO VAT", "NON-VAT", "NOT VATABLE", "C" },
            Where = new List<string> { TaxWindowSlot, TaxWindowAnnotation, TaxWindowSection }
        },
        new TaxMarkerRule
        {
            TaxType = TaxTypeZeroRated,
            Markers = new List<string> { "ZERO", "ZERO RATED", "ZR", "B" },
            Where = new List<string> { TaxWindowSlot, TaxWindowAnnotation, TaxWindowSection }
        },
        new TaxMarkerRule
        {
            TaxType = TaxTypeStandard,
            Markers = new List<string> { "VAT", "STANDARD", "STD", "A" },
            Where = new List<string> { TaxWindowSlot, TaxWindowAnnotation }
        }
    };

    public static List<string> DefaultDateLabels() => new() { "Date", "Dated" };    public static List<string> DefaultDateFormats() => new()
    {
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
        "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "dd MMM yyyy HH:mm", "dd MMM yyyy"
    };

    public static List<string> DefaultVatNumberLabels() => new() { "Customer VAT", "Buyer VAT", "Client VAT" };

    public static List<string> DefaultTinLabels() => new() { "Customer TIN", "Buyer TIN", "Client TIN" };

    public static List<string> DefaultPhoneLabels() => new() { "Phone", "Tel" };

    public static List<string> DefaultAddressLabels() => new() { "Address" };

    public static List<string> DefaultBuyerNameLiterals() => new()
    {
        "Cash Customer", "Walk-in Customer", "Walk in Customer"
    };

    public static List<string> DefaultInvoiceNumberKeywords() => new()
    {
        "Invoice", "Order", "Receipt", "INV"
    };

    public static List<string> DefaultBuyerNameKeywords() => new() { "Buyer", "Customer", "Client" };

    public static List<string> DefaultNonItemKeywords() => new()
    {
        "total", "subtotal", "sub total", "tax", "vat", "balance", "change",
        "tendered", "paid", "payment", "discount", "rounding", "items count",
        "order no", "invoice"
    };

    public static List<string> DefaultItemTableHeaderRequires() => new() { "DESCRIPTION", "QUANTITY" };

    public static List<string> DefaultItemTableHeaderAnyOf() => new()
    {
        "EXCLUSIVETOTAL", "INCLUSIVETOTAL", "EXCLTOTAL", "INCLTOTAL"
    };

    public static List<string> DefaultItemTableEndMarkers() => new()
    {
        "banking details", "total", "sub total", "grand total", "balance due",
        "payment", "terms", "thank you"
    };
}

/// <summary>
/// The line-shape patterns. Every one is a .NET regex and is guarded by a match
/// timeout, because a hand-edited pattern can backtrack forever on an odd line and
/// a stalled parse thread holds up the whole till. Groups named qty, price, amount,
/// disc, vat, exclusive, inclusive, name and value are contracts the parser reads.
/// </summary>
public sealed class ParsePatterns
{
    /// <summary>Matches a document number printed with its label. Needs the "value" group.</summary>
    public string InvoiceNumber { get; set; } =
        @"(?i)\b(?:invoice\s*(?:no|number)?|order\s*no\.?|number)\s*[:#\-]?\s*(?<value>[A-Z]{2,5}[\-/]?\d{3,}|\d{4,})";

    /// <summary>
    /// A collapsed invoice-table row: name, qty, price, discount %, VAT % and totals.
    /// Needs the name, qty, price, disc, vat and exclusive groups; inclusive is optional.
    /// So is <c>tax</c> — a row that ends in a tax-code letter puts it there, which is how
    /// a POS with an exempt column is read without guessing from a 0% figure.
    /// </summary>
    public string InvoiceRow { get; set; } =
        @"^(?<name>.*?)\s*(?<qty>\d{1,3}(?:,\d{3})*(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?:[A-Z]{1,3}\s*)?" +
        @"(?<price>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*" +
        @"(?<disc>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*%\s*" +
        @"(?<vat>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*%\s*" +
        @"(?:[A-Z]{1,3}\s*)?(?<exclusive>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)" +
        @"(?:\s*(?:[A-Z]{1,3}\s*)?(?<inclusive>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?))?" +
        @"(?:\s+(?<tax>[A-Z]{1,3}))?$";

    /// <summary>First line of a two-line item: "unit price x quantity". Needs price and qty.</summary>
    public string TwoLineQuantity { get; set; } =
        @"^(?<price>[\d,]+(?:\.\d+)?)\s*[xX]\s*(?<qty>[\d,]+(?:\.\d+)?)$";

    /// <summary>Second line of a two-line item: "name *line total". Needs name and amount.</summary>
    public string TwoLineNameAmount { get; set; } =
        @"^(?<name>.+?)\s*\*?\s*(?<amount>-?[\d,]+\.\d{2})$";

    /// <summary>Detects an amount written as "M1 234.56", i.e. the Lesotho loti convention.</summary>
    public string CurrencyBeforeAmount { get; set; } =
        @"(?<![A-Za-z])M\s*\d{1,3}(?:,\d{3})*\.\d{2}(?![0-9])";

    /// <summary>Starts a line that is a buyer field label rather than a buyer name.</summary>
    public string BuyerLabel { get; set; } =
        @"^(VAT|TIN|No\.?|Name|Address|Email|Phone|Telephone)\b";

    /// <summary>An amount printed on its own line, e.g. under "Total VAT:". Needs to match the whole line.</summary>
    public string AmountLine { get; set; } =
        @"^[A-Z]{0,3}\s*-?\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?$";

    /// <summary>Finds a buyer email anywhere in the job.</summary>
    public string Email { get; set; } =
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}";

    public static ParsePatterns Defaults() => new();

    /// <summary>
    /// The parser reads named groups, so a pattern that compiles can still be unusable.
    /// This is the contract, checked on hand-edited config and on profile import.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        Check(errors, "invoiceNumber", InvoiceNumber, "value");
        Check(errors, "invoiceRow", InvoiceRow, "name", "qty", "price", "disc", "vat", "exclusive");
        Check(errors, "twoLineQuantity", TwoLineQuantity, "price", "qty");
        Check(errors, "twoLineNameAmount", TwoLineNameAmount, "name", "amount");
        Check(errors, "currencyBeforeAmount", CurrencyBeforeAmount);
        Check(errors, "buyerLabel", BuyerLabel);
        Check(errors, "amountLine", AmountLine);
        Check(errors, "email", Email);

        return errors;
    }

    private static void Check(List<string> errors, string name, string pattern, params string[] groups)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            errors.Add($"parse.patterns.{name} is empty.");
            return;
        }

        try
        {
            var regex = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));

            if (groups.Length == 0)
            {
                return;
            }

            var declared = new HashSet<string>(regex.GetGroupNames(), StringComparer.Ordinal);
            var missing = groups.Where(group => !declared.Contains(group)).ToArray();
            if (missing.Length > 0)
            {
                errors.Add($"parse.patterns.{name} is missing the group(s) {string.Join(", ", missing)}.");
            }
        }
        catch (ArgumentException ex)
        {
            errors.Add($"parse.patterns.{name} is not a valid regular expression: {ex.Message}");
        }
    }
}

/// <summary>
/// One step in reading a line's VAT status off the paper: the bucket its markers name,
/// and where on the line those markers are allowed to match. Rules are evaluated in the
/// order they are written, so the list is the precedence.
/// </summary>
public sealed class TaxMarkerRule
{
    /// <summary>STANDARD, ZERO_RATED or EXEMPT — the bucket the gateway maps to a ZIMRA tax ID.</summary>
    public string TaxType { get; set; } = ParseConfig.TaxTypeStandard;

    /// <summary>
    /// Marker text, matched case-insensitively. Punctuation and repeated spaces are ignored,
    /// so "zero rated", "Zero-Rated" and "ZERO  RATED" are one marker.
    /// </summary>
    public List<string> Markers { get; set; } = new();

    /// <summary>Windows a marker may match in: slot, annotation, section. Empty means all three.</summary>
    public List<string> Where { get; set; } = new();
}

/// <summary>
/// Numeric guard rails. They decide how much disagreement still counts as "this line
/// parsed", so they are the difference between accepting a rounded POS total and
/// inventing one — the printed-total check itself is not configurable.
/// </summary>
public sealed class ParseLimits
{
    /// <summary>Largest accepted difference, in currency units, between a computed and printed amount.</summary>
    public decimal LineTolerance { get; set; } = 0.05m;

    /// <summary>Largest accepted difference as a fraction of the amount, for heavily rounded lines.</summary>
    public decimal RelativeTolerance { get; set; } = 0.02m;

    /// <summary>Column layouts scoring below this parse with a warning instead of silently.</summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>A line needs at least this many whitespace-separated tokens to be an item.</summary>
    public int MinItemTokens { get; set; } = 4;

    public int MaxInvoiceNumberLength { get; set; } = 40;
    public int MaxBuyerNameLength { get; set; } = 100;
    public int MinIdentifierLength { get; set; } = 4;
    public int ValidYearFrom { get; set; } = 2000;
    public int ValidYearTo { get; set; } = 2100;

    /// <summary>Per-match budget for every pattern. Guards a till against a backtracking pattern.</summary>
    public int RegexTimeoutMilliseconds { get; set; } = 1000;

    public static ParseLimits Defaults() => new();
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
