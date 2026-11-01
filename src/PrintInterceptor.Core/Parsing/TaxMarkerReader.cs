using System.Text;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;

namespace PrintInterceptor.Core.Parsing;

/// <summary>
/// Reads the VAT status a printed line declares for itself. A POS states tax in three
/// places only — a code in or after the amount column, a bracketed note, or a header over
/// a block of items — so those are the only windows matched here. The product description
/// is never read, which is what keeps "Zero Sugar Coke" a taxable item.
/// </summary>
public sealed class TaxMarkerReader
{
    private const int MaxSlotTokens = 4;
    private const int MinSubstringMarkerLength = 3;

    private static readonly Regex AnnotationPattern = new(
        @"[\(\[\{<]([^\(\)\[\]\{\}<>]{1,60})[\)\]\}>]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingCodePattern = new(
        @"[A-Za-z]{1,3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<Rule> _rules;

    public TaxMarkerReader(ParseConfig config)
    {
        DefaultTaxType = ParseConfig.CanonicalTaxType(config.DefaultTaxType) ?? ParseConfig.TaxTypeStandard;

        var rules = new List<Rule>();
        foreach (var rule in config.TaxRules)
        {
            var taxType = ParseConfig.CanonicalTaxType(rule.TaxType);
            if (taxType is null)
            {
                continue;
            }

            var windows = (rule.Where ?? new List<string>())
                .Select(ParseConfig.CanonicalTaxWindow)
                .Where(window => window is not null)
                .Select(window => window!)
                .ToHashSet(StringComparer.Ordinal);

            if (windows.Count == 0)
            {
                windows.Add(ParseConfig.TaxWindowSlot);
                windows.Add(ParseConfig.TaxWindowAnnotation);
                windows.Add(ParseConfig.TaxWindowSection);
            }

            var markers = (rule.Markers ?? new List<string>())
                .Where(marker => !string.IsNullOrWhiteSpace(marker))
                .Select(marker => (original: marker.Trim(), collapsed: Collapse(marker)))
                .Where(marker => marker.collapsed.Length > 0)
                .ToList();

            if (markers.Count > 0)
            {
                rules.Add(new Rule(taxType, markers, windows));
            }
        }

        // The legacy name-substring knobs stay out of profiles, but they state the same
        // thing, so they join the vocabulary last: lowest precedence, and only in the
        // windows this reader has, never as a claim over the lines below a header.
        AddLegacySymbol(rules, config.NonTaxSymbol, ParseConfig.TaxTypeExempt);
        AddLegacySymbol(rules, config.TaxSymbol, ParseConfig.TaxTypeStandard);

        _rules = rules;
    }

    /// <summary>The bucket for a line that declares nothing.</summary>
    public string DefaultTaxType { get; }

    /// <summary>True when a profile actually gave the parser something to look for.</summary>
    public bool HasMarkers => _rules.Count > 0;

    /// <summary>
    /// The bucket named by the line's own markers, or null when it declares none. A marker
    /// that says more wins — the longest hit — because "NON-VAT" contains the code "VAT" and
    /// is the better statement. Rule order only breaks a tie.
    /// </summary>
    public TaxMarkerMatch? Read(string line, IReadOnlyList<string> slotTokens)
    {
        List<TaxMarkerMatch> hits = new();
        foreach (var rule in _rules)
        {
            var hit = MatchRule(rule, line, slotTokens);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var winner = hits
            .Select((hit, order) => (hit, order))
            .OrderByDescending(candidate => candidate.hit.Marker.Length)
            .ThenBy(candidate => candidate.order)
            .First().hit;

        var conflicting = hits.FirstOrDefault(hit => !string.Equals(hit.TaxType, winner.TaxType, StringComparison.Ordinal));
        return conflicting is null
            ? winner with { ConflictsWith = null }
            : winner with { ConflictsWith = $"{conflicting.TaxType} via '{conflicting.Marker}'" };
    }

    /// <summary>The bucket a header line claims for the item lines below it, or null.</summary>
    public string? ReadSection(string line)
    {
        var collapsed = Collapse(line);
        if (collapsed.Length == 0)
        {
            return null;
        }

        TaxMarkerMatch? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Windows.Contains(ParseConfig.TaxWindowSection))
            {
                continue;
            }

            foreach (var marker in rule.Markers)
            {
                // A header has to be about the block, not merely mention a word in passing.
                if (marker.Collapsed.Length >= MinSubstringMarkerLength &&
                    collapsed.Contains(marker.Collapsed, StringComparison.Ordinal) &&
                    (best is null || marker.Original.Length > best.Marker.Length))
                {
                    best = new TaxMarkerMatch(rule.TaxType, marker.Original, ParseConfig.TaxWindowSection, null);
                }
            }
        }

        return best?.TaxType;
    }

    private static TaxMarkerMatch? MatchRule(Rule rule, string line, IReadOnlyList<string> slotTokens)
    {
        TaxMarkerMatch? best = null;

        if (rule.Windows.Contains(ParseConfig.TaxWindowSlot))
        {
            foreach (var token in slotTokens)
            {
                var collapsed = Collapse(token);
                if (collapsed.Length == 0)
                {
                    continue;
                }

                foreach (var marker in rule.Markers)
                {
                    if (string.Equals(collapsed, marker.Collapsed, StringComparison.Ordinal))
                    {
                        best = Longer(best, new TaxMarkerMatch(rule.TaxType, marker.Original, ParseConfig.TaxWindowSlot, null));
                        break;
                    }
                }
            }
        }

        if (rule.Windows.Contains(ParseConfig.TaxWindowAnnotation))
        {
            foreach (Match annotation in AnnotationPattern.Matches(line))
            {
                var collapsed = Collapse(annotation.Groups[1].Value);
                if (collapsed.Length == 0)
                {
                    continue;
                }

                foreach (var marker in rule.Markers)
                {
                    if (marker.Collapsed.Length >= MinSubstringMarkerLength &&
                        collapsed.Contains(marker.Collapsed, StringComparison.Ordinal))
                    {
                        best = Longer(best, new TaxMarkerMatch(rule.TaxType, marker.Original, ParseConfig.TaxWindowAnnotation, null));
                        break;
                    }
                }
            }
        }

        return best;
    }

    private static TaxMarkerMatch Longer(TaxMarkerMatch? current, TaxMarkerMatch candidate) =>
        current is null || candidate.Marker.Length > current.Marker.Length ? candidate : current;

    private static void AddLegacySymbol(List<Rule> rules, string? symbol, string taxType)
    {
        var collapsed = Collapse(symbol ?? "");
        if (collapsed.Length == 0)
        {
            return;
        }

        rules.Add(new Rule(
            taxType,
            new[] { (symbol!.Trim(), collapsed) },
            new HashSet<string>(StringComparer.Ordinal)
            {
                ParseConfig.TaxWindowSlot,
                ParseConfig.TaxWindowAnnotation
            }));
    }

    /// <summary>
    /// What is left of a line once its columns are read: a tax code printed after the amount,
    /// or glued to it ("10.00A"), is a declaration; an amount or a currency is not.
    /// </summary>
    public static IReadOnlyList<string> SlotTokens(string[] tokens, int amountIndex, ParseConfig config)
    {
        var slot = new List<string>(MaxSlotTokens);

        if (amountIndex >= 0 && amountIndex < tokens.Length)
        {
            var glued = TrailingCodePattern.Match(tokens[amountIndex]).Value;
            if (glued.Length > 0 && !IsCurrencyToken(glued, config))
            {
                slot.Add(glued);
            }
        }

        for (var i = amountIndex + 1; i < tokens.Length && slot.Count < MaxSlotTokens; i++)
        {
            var token = tokens[i];
            if (token.Length == 0 || IsCurrencyToken(token, config) || LooksLikeMoney(token))
            {
                continue;
            }

            slot.Add(token);
        }

        return slot;
    }

    private static bool LooksLikeMoney(string token) => ReceiptParser.TryParseMoney(token, out _);

    private static bool IsCurrencyToken(string token, ParseConfig config)
    {
        var collapsed = Collapse(token);
        return collapsed.Length > 0 &&
               config.Currencies.Any(currency =>
                   string.Equals(Collapse(currency.Keyword), collapsed, StringComparison.Ordinal));
    }

    /// <summary>
    /// Letters and digits only, upper-cased, so "Zero-Rated", "zero rated" and "ZERORATED"
    /// are one marker. Profiles are hand-written and till fonts are not consistent.
    /// </summary>
    public static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private sealed record Rule(
        string TaxType,
        IReadOnlyList<(string Original, string Collapsed)> Markers,
        IReadOnlySet<string> Windows);
}

/// <summary>
/// A tax bucket a marker claimed for a line, with the marker and window that said it.
/// <see cref="ConflictsWith"/> names a competing claim, which the parser reports instead
/// of acting on: a sale is never held because its VAT status is unclear.
/// </summary>
public sealed record TaxMarkerMatch(
    string TaxType,
    string Marker,
    string Window,
    string? ConflictsWith);
