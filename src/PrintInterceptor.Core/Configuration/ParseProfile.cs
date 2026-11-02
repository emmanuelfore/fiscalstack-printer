using System.Text.Json.Serialization;

namespace PrintInterceptor.Core.Configuration;

/// <summary>
/// A named receipt-layout rule set, stored as a JSON file that can be shared between
/// tills. Picking a profile writes its rules into <see cref="ParseConfig"/>, so the
/// running configuration stays self-sufficient even if the profile file is removed.
/// A profile describes where text is found. It cannot change what must add up: the
/// qty × price check, the printed-total guard and the column scoring stay in the parser.
/// </summary>
public sealed class ParseProfile
{
    /// <summary>Stable identifier and file name, e.g. "sage". Letters, digits and dashes.</summary>
    public string Name { get; set; } = "";

    /// <summary>Name shown in Settings, e.g. "Sage Business Cloud".</summary>
    public string Title { get; set; } = "";

    /// <summary>Which POS and layout this profile was written for.</summary>
    public string Description { get; set; } = "";

    /// <summary>Free-form, for telling two revisions of the same POS apart.</summary>
    public string Version { get; set; } = "1";

    public ParseProfileRules Rules { get; set; } = new();

    /// <summary>File this profile was read from. Empty for a profile not yet saved.</summary>
    [JsonIgnore]
    public string FilePath { get; set; } = "";

    /// <summary>True when shipped inside the application folder rather than imported.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; set; }

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? (string.IsNullOrWhiteSpace(Name) ? "Untitled profile" : Name) : Title;
}

/// <summary>
/// Every field is optional: a profile states only what differs from the built-in rules,
/// which are themselves the Sage layout. Field names must stay identical to the
/// <see cref="ParseConfig"/> properties they write, which a unit test enforces.
/// </summary>
public sealed class ParseProfileRules
{
    public string? ProductStartLine { get; set; }
    public string? ProductEndLine { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTIN { get; set; }
    public string? CustomerVATNumber { get; set; }
    public string? CustomerTelephoneNumber { get; set; }

    public List<string>? DateLabels { get; set; }
    public List<string>? DateFormats { get; set; }
    public List<string>? VatNumberLabels { get; set; }
    public List<string>? TinLabels { get; set; }
    public List<string>? PhoneLabels { get; set; }
    public List<string>? AddressLabels { get; set; }
    public List<string>? BuyerNameLiterals { get; set; }
    public List<string>? InvoiceNumberKeywords { get; set; }
    public List<string>? BuyerNameKeywords { get; set; }
    public List<string>? NonItemKeywords { get; set; }
    public List<string>? ItemTableHeaderRequires { get; set; }
    public List<string>? ItemTableHeaderAnyOf { get; set; }
    public List<string>? ItemTableEndMarkers { get; set; }

    /// <summary>Bucket for a line with no tax marker. Reading tax off the paper is layout; the rate is not.</summary>
    public string? DefaultTaxType { get; set; }

    /// <summary>The whole marker list, in precedence order. Stating it replaces the built-in list.</summary>
    public List<TaxMarkerRule>? TaxRules { get; set; }

    /// <summary>
    /// The correction vocabulary. A profile that states only some of it keeps the built-in
    /// defaults for the rest, so a POS that prints "Reversal Ref" for the original invoice
    /// does not also have to restate every credit-note word.
    /// </summary>
    public CorrectionVocabulary? Corrections { get; set; }

    public ParseProfilePatterns? Patterns { get; set; }
    public ParseProfileLimits? Limits { get; set; }

    /// <summary>Writes the values this profile states onto the live rule set.</summary>
    public void ApplyTo(ParseConfig config)
    {
        if (ProductStartLine is not null) config.ProductStartLine = ProductStartLine;
        if (ProductEndLine is not null) config.ProductEndLine = ProductEndLine;
        if (InvoiceNumber is not null) config.InvoiceNumber = InvoiceNumber;
        if (CustomerName is not null) config.CustomerName = CustomerName;
        if (CustomerAddress is not null) config.CustomerAddress = CustomerAddress;
        if (CustomerTIN is not null) config.CustomerTIN = CustomerTIN;
        if (CustomerVATNumber is not null) config.CustomerVATNumber = CustomerVATNumber;
        if (CustomerTelephoneNumber is not null) config.CustomerTelephoneNumber = CustomerTelephoneNumber;

        if (DateLabels is not null) config.DateLabels = DateLabels;
        if (DateFormats is not null) config.DateFormats = DateFormats;
        if (VatNumberLabels is not null) config.VatNumberLabels = VatNumberLabels;
        if (TinLabels is not null) config.TinLabels = TinLabels;
        if (PhoneLabels is not null) config.PhoneLabels = PhoneLabels;
        if (AddressLabels is not null) config.AddressLabels = AddressLabels;
        if (BuyerNameLiterals is not null) config.BuyerNameLiterals = BuyerNameLiterals;
        if (InvoiceNumberKeywords is not null) config.InvoiceNumberKeywords = InvoiceNumberKeywords;
        if (BuyerNameKeywords is not null) config.BuyerNameKeywords = BuyerNameKeywords;
        if (NonItemKeywords is not null) config.NonItemKeywords = NonItemKeywords;
        if (ItemTableHeaderRequires is not null) config.ItemTableHeaderRequires = ItemTableHeaderRequires;
        if (ItemTableHeaderAnyOf is not null) config.ItemTableHeaderAnyOf = ItemTableHeaderAnyOf;
        if (ItemTableEndMarkers is not null) config.ItemTableEndMarkers = ItemTableEndMarkers;

        if (DefaultTaxType is not null) config.DefaultTaxType = DefaultTaxType;

        // Copied rather than shared: applying a profile twice must not let a later edit
        // of the live rules reach back into the profile object it came from.
        if (TaxRules is not null)
        {
            config.TaxRules = TaxRules
                .Select(rule => new TaxMarkerRule
                {
                    TaxType = rule.TaxType,
                    Markers = (rule.Markers ?? new List<string>()).ToList(),
                    Where = (rule.Where ?? new List<string>()).ToList()
                })
                .ToList();
        }

        Patterns?.ApplyTo(config.Patterns);
        Limits?.ApplyTo(config.Limits);

        if (Corrections is not null) config.Corrections = CopyVocabulary(Corrections);
    }

    /// <summary>
    /// Deep copy for applying, so a later edit of the live vocabulary cannot reach back into
    /// the profile object it came from. A list the profile leaves out (or writes as null) keeps
    /// its built-in default; an explicit empty list means "this POS never prints the word".
    /// Keywords are upper-cased here because the parser compares them with the document's own
    /// upper-cased text.
    /// </summary>
    private static CorrectionVocabulary CopyVocabulary(CorrectionVocabulary? source)
    {
        var copy = new CorrectionVocabulary
        {
            CreditNoteKeywords = KeywordList(source?.CreditNoteKeywords, CorrectionVocabulary.DefaultCreditNoteKeywords()),
            DebitNoteKeywords = KeywordList(source?.DebitNoteKeywords, CorrectionVocabulary.DefaultDebitNoteKeywords()),
            CreditNoteNumberPrefixes = KeywordList(source?.CreditNoteNumberPrefixes, new List<string> { "CR", "CN" }),
            OriginalInvoiceLabels = CleanList(source?.OriginalInvoiceLabels, CorrectionVocabulary.DefaultOriginalInvoiceLabels()),
            ReasonLabels = CleanList(source?.ReasonLabels, CorrectionVocabulary.DefaultReasonLabels()),
            NegativeTotalMeansCreditNote = source?.NegativeTotalMeansCreditNote ?? true
        };
        return copy;
    }

    private static List<string> KeywordList(List<string>? entries, List<string> fallback) => entries switch
    {
        null => fallback.ToList(),
        _ => entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim().ToUpperInvariant())
            .Distinct()
            .ToList()
    };

    private static List<string> CleanList(List<string>? entries, List<string> fallback) => entries switch
    {
        null => fallback.ToList(),
        _ => entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct()
            .ToList()
    };
}

/// <summary>Regex overrides. Names and groups are the contract checked by <see cref="ParsePatterns.Validate"/>.</summary>
public sealed class ParseProfilePatterns
{
    public string? InvoiceNumber { get; set; }
    public string? InvoiceRow { get; set; }
    public string? TwoLineQuantity { get; set; }
    public string? TwoLineNameAmount { get; set; }
    public string? CurrencyBeforeAmount { get; set; }
    public string? BuyerLabel { get; set; }
    public string? AmountLine { get; set; }
    public string? Email { get; set; }

    public void ApplyTo(ParsePatterns patterns)
    {
        if (InvoiceNumber is not null) patterns.InvoiceNumber = InvoiceNumber;
        if (InvoiceRow is not null) patterns.InvoiceRow = InvoiceRow;
        if (TwoLineQuantity is not null) patterns.TwoLineQuantity = TwoLineQuantity;
        if (TwoLineNameAmount is not null) patterns.TwoLineNameAmount = TwoLineNameAmount;
        if (CurrencyBeforeAmount is not null) patterns.CurrencyBeforeAmount = CurrencyBeforeAmount;
        if (BuyerLabel is not null) patterns.BuyerLabel = BuyerLabel;
        if (AmountLine is not null) patterns.AmountLine = AmountLine;
        if (Email is not null) patterns.Email = Email;
    }
}

public sealed class ParseProfileLimits
{
    public decimal? LineTolerance { get; set; }
    public decimal? RelativeTolerance { get; set; }
    public double? MinConfidence { get; set; }
    public int? MinItemTokens { get; set; }
    public int? MaxInvoiceNumberLength { get; set; }
    public int? MaxBuyerNameLength { get; set; }
    public int? MinIdentifierLength { get; set; }
    public int? ValidYearFrom { get; set; }
    public int? ValidYearTo { get; set; }
    public int? RegexTimeoutMilliseconds { get; set; }

    public void ApplyTo(ParseLimits limits)
    {
        if (LineTolerance is not null) limits.LineTolerance = LineTolerance.Value;
        if (RelativeTolerance is not null) limits.RelativeTolerance = RelativeTolerance.Value;
        if (MinConfidence is not null) limits.MinConfidence = MinConfidence.Value;
        if (MinItemTokens is not null) limits.MinItemTokens = MinItemTokens.Value;
        if (MaxInvoiceNumberLength is not null) limits.MaxInvoiceNumberLength = MaxInvoiceNumberLength.Value;
        if (MaxBuyerNameLength is not null) limits.MaxBuyerNameLength = MaxBuyerNameLength.Value;
        if (MinIdentifierLength is not null) limits.MinIdentifierLength = MinIdentifierLength.Value;
        if (ValidYearFrom is not null) limits.ValidYearFrom = ValidYearFrom.Value;
        if (ValidYearTo is not null) limits.ValidYearTo = ValidYearTo.Value;
        if (RegexTimeoutMilliseconds is not null) limits.RegexTimeoutMilliseconds = RegexTimeoutMilliseconds.Value;
    }
}
