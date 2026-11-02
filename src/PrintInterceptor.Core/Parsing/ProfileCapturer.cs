using System.Globalization;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;

namespace PrintInterceptor.Core.Parsing;

/// <summary>One field of the receipt, with the line the till read it from (0 = not shown here).</summary>
public sealed record CaptureField(string Name, string? Value, int Line)
{
    public bool Found => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// One layout rule the capture suggests, carrying the <em>whole</em> list it would write: a profile
/// states a list in full, so a suggestion is the entries this till already has plus the one this
/// receipt needs. <see cref="Because"/> and <see cref="Line"/> are what the operator checks it
/// against before saving.
/// </summary>
public sealed record CaptureSuggestion(
    string Property,
    string Title,
    IReadOnlyList<string> Values,
    string Because,
    int Line);

/// <summary>What one set of layout rules makes of the same receipt.</summary>
public sealed record CaptureReading(ParseResult Result, IReadOnlyList<CaptureField> Fields)
{
    public ParsedReceipt? Receipt => Result.Receipt;

    public bool IsSuccess => Result.Status == ParseStatus.Parsed;

    public int Items => Result.Receipt?.Items.Count ?? 0;
}

/// <summary>
/// The result of reading one captured job against the till's current rules: what was found, what the
/// suggested additions would find, and the profile that carries only those additions.
/// </summary>
public sealed record ProfileCapture(
    IReadOnlyList<string> Lines,
    CaptureReading Current,
    CaptureReading Suggested,
    IReadOnlyList<CaptureSuggestion> Suggestions,
    ParseProfileRules Rules,
    IReadOnlyList<string> Notes)
{
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True when the suggested rules read strictly more of this receipt than the current ones.</summary>
    public bool Improved => !Current.IsSuccess && Suggested.IsSuccess || Suggested.Items > Current.Items;
}

/// <summary>
/// Turns a real receipt the till already captured into a parse profile. This is the onboarding path
/// for an unknown POS: instead of hand-writing labels and markers, the operator opens one of the
/// day's jobs, sees which line each field was read from and which fields were missed, and saves a
/// profile containing only what the built-in rules do not already state.
///
/// Every suggestion is aimed at a rule the parser genuinely reads, and the suggested rules are run
/// over the same text so the operator sees the difference before saving. A suggestion is only made
/// when it cannot change what a document <em>is</em>: the correction words, the qty × price check,
/// the printed-total guard and the column scoring stay outside the capture, because a profile that
/// turns a sale into a credit note is a filing error, not a layout fix.
/// </summary>
public static class ProfileCapturer
{
    /// <summary>Column words that name the item description on a table header.</summary>
    private static readonly string[] NameColumns = { "DESCRIPTION", "ITEM", "PRODUCT", "ARTICLE", "GOODS", "SERVICE" };

    private static readonly string[] QuantityColumns = { "QUANTITY", "QTY", "UNITS", "UNIT" };

    private static readonly string[] TotalColumns = { "TOTAL", "AMOUNT", "VALUE" };

    /// <summary>Words that start the block a receipt prints after its items.</summary>
    private static readonly string[] FooterWords =
    [
        "total", "sub total", "subtotal", "balance", "amount due", "amount received", "cash", "card",
        "payment", "paid", "change", "vat", "tax", "discount", "rounding", "banking", "terms",
        "thank", "signature", "prepared by", "authorised", "items count"
    ];

    private static readonly string[] DocumentNumberWords =
        ["invoice", "order", "slip", "receipt", "doc", "document", "bill", "ticket", "docket", "transaction"];

    /// <summary>
    /// Words that, on a document already announced as a correction, mean the number after them is
    /// the sale being reversed rather than this slip's own.
    /// </summary>
    private static readonly string[] OriginalReferenceWords =
        ["ORIGINAL", "REVERSAL", "REVERSED", "REVERSE", "CANCELLED", "AGAINST", "REFERENCE", "REF", "STORNO", "RELATED", "REPLACES", "PREVIOUS"];

    private static readonly string[] CorrectionWords =
        ["CREDIT NOTE", "DEBIT NOTE", "REFUND", "RETURN", "SALES RETURN", "REVERSAL", "REVERSED", "VOID", "STORNO", "CANCELLED"];

    private static readonly Regex DatePattern = new(
        @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex LooseNumberPattern = new(
        @"(?<label>[A-Za-z][A-Za-z ]{1,20}?)\s+(?<value>[A-Z]{2,6}[\-/]?\d{3,}|\d{4,})(?![\d.])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    public static ProfileCapture Capture(string text, ParseConfig current)
    {
        var lines = Split(text);
        var asIs = Read(new ReceiptParser(current).Parse(text), lines);

        var rules = new ParseProfileRules();
        var suggestions = new List<CaptureSuggestion>();
        var notes = new List<string>();

        SuggestTable(rules, suggestions, lines, current);
        SuggestItemBlock(rules, suggestions, lines, current);
        SuggestDocumentNumber(rules, suggestions, lines, current);
        SuggestDate(rules, suggestions, lines, current);
        SuggestBuyerLabels(rules, suggestions, lines, current);
        SuggestOriginalReference(rules, suggestions, lines, current);
        AddTaxCodeNote(notes, lines, current);
        AddCorrectionNote(notes, lines, current);
        AddGuardNotes(notes, lines);

        var candidate = LayoutClone(current);
        rules.ApplyTo(candidate);
        var suggested = Read(new ReceiptParser(candidate).Parse(text), lines);
        AddMismatchNote(notes, asIs);
        AddMismatchNote(notes, suggested);
        AddUnreadNotes(notes, suggestions, suggested);

        return new ProfileCapture(lines, asIs, suggested, suggestions, rules, notes);
    }

    private static readonly Dictionary<string, string> FieldByProperty = new(StringComparer.Ordinal)
    {
        ["customerName"] = "Buyer name",
        ["customerTIN"] = "Buyer TIN",
        ["customerVatNumber"] = "Buyer VAT",
        ["customerTelephoneNumber"] = "Buyer phone",
        ["customerAddress"] = "Buyer address",
        ["invoiceNumber"] = "Invoice number",
        ["invoiceNumberKeywords"] = "Invoice number",
        ["dateLabels"] = "Document date",
        ["dateFormats"] = "Document date",
        ["itemTableHeaderRequires"] = "Item lines",
        ["itemTableHeaderAnyOf"] = "Item lines",
        ["itemTableEndMarkers"] = "Item lines",
        ["productStartLine"] = "Item lines",
        ["productEndLine"] = "Item lines",
        ["corrections.originalInvoiceLabels"] = "Reverses"
    };

    /// <summary>
    /// A suggestion the preview still cannot read is the operator's signal that this one receipt is
    /// not enough evidence, so it is said here rather than left for the next till to discover. When
    /// nothing at all parsed, only the item block is named: the other fields are unread because the
    /// document has no items to anchor them, not because their labels are wrong.
    /// </summary>
    private static void AddUnreadNotes(
        List<string> notes,
        IReadOnlyList<CaptureSuggestion> suggestions,
        CaptureReading suggested)
    {
        if (suggestions.Count == 0)
        {
            return;
        }

        if (suggested.Items == 0)
        {
            if (PrintedTotalMismatch(suggested.Result))
            {
                return;
            }

            notes.Add(
                "Even with these additions the till finds no item lines, so nothing else on this receipt is read yet. " +
                "The words that name the item table are the block to settle first — try a second receipt from this POS, " +
                "or set the header under parse.itemTableHeader in Settings.");
            return;
        }

        var unread = suggestions
            .Select(suggestion => (Property: suggestion.Property, Field: FieldByProperty.GetValueOrDefault(suggestion.Property)))
            .Where(entry => entry.Field is not null)
            .GroupBy(entry => entry.Field)
            .Where(group => !suggested.Fields.Any(field =>
                field.Name == group.Key && field.Found))
            .Select(group => group.Key)
            .ToList();

        if (unread.Count == 0)
        {
            return;
        }

        notes.Add(
            "Even with these additions the till does not read: " + string.Join(", ", unread) +
            ". One receipt is not enough evidence for them — try a second receipt from this POS before saving, " +
            "or leave the field out of the profile.");
    }

    private static CaptureReading Read(ParseResult result, IReadOnlyList<string> lines)
    {
        var receipt = result.Receipt;
        var printedDate = receipt?.Date is { } dated
            ? dated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : null;
        var fields = new List<CaptureField>
        {
            new("Invoice number", receipt?.InvoiceNumber, LineOf(lines, receipt?.InvoiceNumber)),
            new("Document date", printedDate, DateLine(lines)),
            new("Document type", receipt?.TransactionType, LineOf(lines, receipt?.TransactionType)),
            new("Reverses", receipt?.RelatedInvoiceNumber, LineOf(lines, receipt?.RelatedInvoiceNumber)),
            new("Reason", receipt?.CreditNoteReason, LineOf(lines, receipt?.CreditNoteReason)),
            new("Currency", receipt?.Currency, LineOf(lines, receipt?.Currency)),
            new("Payment method", receipt?.PaymentMethod, LineOf(lines, receipt?.PaymentMethod)),
            new("Total", receipt is null ? null : Money(receipt.Total), 0),
            new("Tax", receipt is null ? null : Money(receipt.TaxTotal), 0),
            new("Item lines", receipt is null or { Items.Count: 0 } ? null : receipt.Items.Count.ToString(CultureInfo.InvariantCulture), 0),
            new("Buyer name", receipt?.Buyer.Name, LineOf(lines, receipt?.Buyer.Name)),
            new("Buyer TIN", receipt?.Buyer.Tin, LineOf(lines, receipt?.Buyer.Tin)),
            new("Buyer VAT", receipt?.Buyer.VatNumber, LineOf(lines, receipt?.Buyer.VatNumber)),
            new("Buyer phone", receipt?.Buyer.Phone, LineOf(lines, receipt?.Buyer.Phone)),
            new("Buyer address", receipt?.Buyer.Address, LineOf(lines, receipt?.Buyer.Address)),
            new("Buyer email", receipt?.Buyer.Email, LineOf(lines, receipt?.Buyer.Email))
        };

        return new CaptureReading(result, fields);
    }

    /// <summary>
    /// The header words that identify the item table and the line that ends it. Without them the
    /// collapsed invoice table is never read, which is the usual reason a new POS yields no items.
    /// </summary>
    private static void SuggestTable(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var collapsed = TaxMarkerReader.Collapse(lines[index]);
            if (collapsed.Length == 0)
            {
                continue;
            }

            if (ReadsAsHeader(collapsed, current))
            {
                // The current rules already find a header in this document; there is nothing to add.
                return;
            }
        }

        foreach (var (name, quantity, total, index) in HeaderCandidates(lines))
        {
            rules.ItemTableHeaderRequires = new List<string> { name, quantity };
            suggestions.Add(new CaptureSuggestion(
                "itemTableHeaderRequires",
                "Item table header must contain",
                rules.ItemTableHeaderRequires,
                $"'{name}' and '{quantity}' are the columns your POS prints; the built-in words are not on that line.",
                index + 1));

            var totals = Append(current.ItemTableHeaderAnyOf, total);
            rules.ItemTableHeaderAnyOf = totals;
            suggestions.Add(new CaptureSuggestion(
                "itemTableHeaderAnyOf",
                "Item table header names its total column as",
                totals,
                $"'{total}' is the total column printed in that header.",
                index + 1));

            var end = EndMarkerAfter(lines, index);
            if (end is not null && !IsCurrentEndMarker(end.Value.Marker, current))
            {
                var markers = Append(current.ItemTableEndMarkers, end.Value.Marker);
                rules.ItemTableEndMarkers = markers;
                suggestions.Add(new CaptureSuggestion(
                    "itemTableEndMarkers",
                    "Item table ends at a line starting with",
                    markers,
                    $"\"{lines[end.Value.Index].Trim()}\" is where the items stop.",
                    end.Value.Index + 1));
            }

            return;
        }
    }

    /// <summary>
    /// The two marker lines that bound the item block for the column parser. Only suggested when the
    /// ones configured for this till are nowhere in the document, which is what makes the parser read
    /// the whole job — header, footer and all — as candidate item lines.
    /// </summary>
    private static void SuggestItemBlock(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        var firstItem = FirstItemLine(lines);

        if (!Appears(lines, current.ProductStartLine))
        {
            var start = StartMarkerFor(lines, firstItem);
            if (start is not null && UsableMarker(lines, start.Value.Line, start.Value.Index))
            {
                rules.ProductStartLine = start.Value.Line;
                suggestions.Add(new CaptureSuggestion(
                    "productStartLine",
                    "Item block starts after",
                    new[] { start.Value.Line },
                    $"\"{start.Value.Line}\" is the line your POS puts above the items; the till looks for \"{current.ProductStartLine}\".",
                    start.Value.Index + 1));
            }
        }

        if (!Appears(lines, current.ProductEndLine))
        {
            var end = EndMarkerAfter(lines, firstItem);
            if (end is not null && UsableMarker(lines, end.Value.Line, end.Value.Index))
            {
                rules.ProductEndLine = end.Value.Line;
                suggestions.Add(new CaptureSuggestion(
                    "productEndLine",
                    "Item block ends at",
                    new[] { end.Value.Line },
                    $"\"{end.Value.Line}\" is the first line after the items, so the totals block is never read as an item.",
                    end.Value.Index + 1));
            }
        }
    }

    /// <summary>
    /// The label this POS prints its document number under. The parser tries that label before any
    /// loose keyword or pattern, so a receipt whose number is missed usually only needs the words it
    /// is printed with — and the number itself is what duplicate protection keys on.
    /// </summary>
    private static void SuggestDocumentNumber(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        var lookedFor = new[] { current.InvoiceNumber }
            .Concat(current.InvoiceNumberKeywords)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            var (label, value) = LabelAndValue(lines[index]);
            if (label is null || value is null || !IsDocumentNumber(value))
            {
                continue;
            }

            if (!DocumentNumberWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (lookedFor.Any(entry => SharesWords(entry, label)))
            {
                continue;
            }

            rules.InvoiceNumber = label;
            suggestions.Add(new CaptureSuggestion(
                "invoiceNumber",
                "Document number label",
                new[] { label },
                $"\"{value}\" is printed after this label, and the till's own label (\"{current.InvoiceNumber}\") is not on the document.",
                index + 1));
            return;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var match = LooseNumberPattern.Match(lines[index]);
            if (!match.Success || !IsDocumentNumber(match.Groups["value"].Value))
            {
                continue;
            }

            var label = match.Groups["label"].Value.Trim();
            if (!DocumentNumberWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
                lookedFor.Any(entry => SharesWords(entry, label)))
            {
                continue;
            }

            var keywords = Append(current.InvoiceNumberKeywords, label.ToUpperInvariant());
            rules.InvoiceNumberKeywords = keywords;
            suggestions.Add(new CaptureSuggestion(
                "invoiceNumberKeywords",
                "Document number keywords",
                keywords,
                $"\"{match.Groups["value"].Value}\" is the document number and only \"{label}\" introduces it.",
                index + 1));
            return;
        }
    }

    /// <summary>The label the date is printed under, and a format for the way this POS writes it.</summary>
    private static void SuggestDate(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        var index = IndexOf(lines, line => DatePattern.IsMatch(line));
        if (index < 0)
        {
            return;
        }

        var match = DatePattern.Match(lines[index]);
        var printed = match.Value;

        if (!current.DateLabels.Any(label => SharesWords(label, lines[index])))
        {
            var (label, _) = LabelAndValue(lines[index]);
            if (label is { Length: > 0 })
            {
                var labels = Append(current.DateLabels, label);
                rules.DateLabels = labels;
                suggestions.Add(new CaptureSuggestion(
                    "dateLabels",
                    "Date labels",
                    labels,
                    $"\"{printed}\" is printed after \"{label}\".",
                    index + 1));
            }
        }

        if (!IsPrintedDateReadable(printed, current.DateFormats))
        {
            var formats = Append(current.DateFormats, DerivedFormats(printed));
            if (formats.Count > current.DateFormats.Count)
            {
                rules.DateFormats = formats;
                suggestions.Add(new CaptureSuggestion(
                    "dateFormats",
                    "Date formats",
                    formats,
                    $"\"{printed}\" is not read by any format the till currently accepts.",
                    index + 1));
            }
        }
    }

    /// <summary>
    /// The buyer fields the document states under a label the till does not look for. Each one is
    /// written as the field's own label rather than as an extra keyword, because the parser tries
    /// that label first and a keyword it reaches earlier can land on the wrong value. A ZIMRA invoice
    /// above the threshold needs the buyer's TIN and VAT number, and those are only filed when the
    /// till can read them off the paper.
    /// </summary>
    private static void SuggestBuyerLabels(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        SuggestLabel(
            suggestions,
            lines,
            "customerVatNumber",
            "Buyer VAT number label",
            current.CustomerVATNumber,
            current.VatNumberLabels,
            label => label.Contains("VAT", StringComparison.OrdinalIgnoreCase) &&
                     !label.Contains("RATE", StringComparison.OrdinalIgnoreCase),
            value => value.Any(char.IsDigit),
            value => rules.CustomerVATNumber = value);

        SuggestLabel(
            suggestions,
            lines,
            "customerTIN",
            "Buyer TIN label",
            current.CustomerTIN,
            current.TinLabels,
            label => label.Contains("TIN", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("ZIMRA", StringComparison.OrdinalIgnoreCase),
            value => value.Count(char.IsLetterOrDigit) >= 4,
            value => rules.CustomerTIN = value);

        SuggestLabel(
            suggestions,
            lines,
            "customerTelephoneNumber",
            "Buyer phone label",
            current.CustomerTelephoneNumber,
            current.PhoneLabels,
            label => label.Contains("PHONE", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("TEL", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("CELL", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("MOBILE", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("WHATSAPP", StringComparison.OrdinalIgnoreCase),
            value => value.Count(char.IsDigit) >= 6,
            value => rules.CustomerTelephoneNumber = value);

        SuggestLabel(
            suggestions,
            lines,
            "customerAddress",
            "Buyer address label",
            current.CustomerAddress,
            current.AddressLabels,
            label => label.Contains("ADDRESS", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("STREET", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("PHYSICAL", StringComparison.OrdinalIgnoreCase),
            value => value.Any(char.IsLetter),
            value => rules.CustomerAddress = value);

        SuggestLabel(
            suggestions,
            lines,
            "customerName",
            "Buyer name label",
            current.CustomerName,
            current.BuyerNameKeywords,
            label => label.Contains("BUYER", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("CUSTOMER", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("CLIENT", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("SOLD TO", StringComparison.OrdinalIgnoreCase),
            value => value.Any(char.IsLetter) && !value.Any(char.IsDigit),
            value => rules.CustomerName = value);
    }

    private static void SuggestLabel(
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        string property,
        string title,
        string configuredLabel,
        List<string> fallbackLabels,
        Func<string, bool> labelLooksLikeField,
        Func<string, bool> valueLooksLikeData,
        Action<string> assign)
    {
        var wanted = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredLabel))
        {
            wanted.Add(configuredLabel);
        }

        wanted.AddRange(fallbackLabels);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (LooksLikeItemRow(line))
            {
                continue;
            }

            var (label, value) = LabelAndValue(line);
            if (label is null || value is null || !labelLooksLikeField(label) || !valueLooksLikeData(value))
            {
                continue;
            }

            // The parser matches a label as a substring of the printed line, so a line its current
            // labels already reach needs no suggestion.
            if (wanted.Any(entry => SharesWords(entry, label)))
            {
                continue;
            }

            assign(label);
            suggestions.Add(new CaptureSuggestion(
                property,
                title,
                new[] { label },
                $"\"{line.Trim()}\" states this field under this label, and the till looks for \"{configuredLabel}\".",
                index + 1));
            return;
        }
    }

    /// <summary>
    /// The label that carries the invoice a correction reverses, which the gateway requires for a
    /// credit note. Only suggested for a document that is demonstrably a correction — it prints a
    /// negative total or says so above the items — because a wrong original-invoice label also hides
    /// the document's own number from duplicate protection.
    /// </summary>
    private static void SuggestOriginalReference(
        ParseProfileRules rules,
        List<CaptureSuggestion> suggestions,
        IReadOnlyList<string> lines,
        ParseConfig current)
    {
        if (!AnnouncesCorrection(lines, current))
        {
            return;
        }

        var firstItem = FirstItemLine(lines);
        for (var index = 0; index <= firstItem && index < lines.Count; index++)
        {
            var (label, value) = LabelAndValue(lines[index]);
            if (label is null || value is null || !IsDocumentNumber(value))
            {
                continue;
            }

            if (!OriginalReferenceWords.Any(word => WholeWord(word, lines[index])))
            {
                continue;
            }

            if (current.Corrections.OriginalInvoiceLabels.Any(entry => SharesWords(entry, label)))
            {
                continue;
            }

            var labels = Append(current.Corrections.OriginalInvoiceLabels, label);
            var vocabulary = rules.Corrections ?? Clone(current.Corrections);
            vocabulary.OriginalInvoiceLabels = labels;
            rules.Corrections = vocabulary;
            suggestions.Add(new CaptureSuggestion(
                "corrections.originalInvoiceLabels",
                "Original invoice labels",
                labels,
                $"\"{label}\" carries the number this document reverses ({value}); without it a credit note is held for the missing reference.",
                index + 1));
            return;
        }
    }

    /// <summary>
    /// Tax codes printed after an amount that no rule names. Which bucket a code means is not
    /// something a receipt can state on its own, so this is reported for the operator to settle in
    /// Settings rather than guessed into a profile.
    /// </summary>
    private static void AddTaxCodeNote(List<string> notes, IReadOnlyList<string> lines, ParseConfig current)
    {
        var known = current.TaxRules
            .SelectMany(rule => rule.Markers ?? new List<string>())
            .Select(marker => marker.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var tokens = SplitTokens(line);
            if (tokens.Length < 3 || AmountTokens(line).Count < 2)
            {
                continue;
            }

            var last = tokens[^1];
            if (last.Length is >= 1 and <= 3 && last.All(char.IsLetter) && last.All(char.IsUpper) &&
                !known.Contains(last))
            {
                unknown.Add(last);
            }
        }

        if (unknown.Count > 0)
        {
            notes.Add(
                "These codes are printed after amounts and name no tax bucket in the current rules: " +
                string.Join(", ", unknown) +
                ". A receipt cannot say which VAT bucket a code means — set it under parse.taxRules in Settings.");
        }
    }

    /// <summary>
    /// A correction word the till does not read. Deliberately a note and not a suggestion: adding a
    /// word to the credit-note vocabulary changes what a document <em>is</em>, and a sale filed as a
    /// return is a filing error rather than a layout fix.
    /// </summary>
    private static void AddCorrectionNote(List<string> notes, IReadOnlyList<string> lines, ParseConfig current)
    {
        var firstItem = Math.Max(FirstItemLine(lines), 1);
        var unread = new SortedSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < firstItem && index < lines.Count; index++)
        {
            foreach (var word in CorrectionWords)
            {
                if (WholeWord(word, lines[index]) &&
                    !current.Corrections.CreditNoteKeywords.Any(keyword => WholeWord(keyword, lines[index])) &&
                    !current.Corrections.DebitNoteKeywords.Any(keyword => WholeWord(keyword, lines[index])))
                {
                    unread.Add(word);
                }
            }
        }

        if (unread.Count > 0)
        {
            notes.Add(
                "This document is headed with " + string.Join(", ", unread) +
                ", which the till does not read as a correction. If it really is a return, add the word under " +
                "parse.corrections in Settings — a profile that guesses this would file ordinary sales as credit notes.");
        }
    }

    /// <summary>
    /// What a profile cannot reach, said plainly: the words that announce a printed total and the
    /// checks that keep a wrong figure off the fiscal record. A capture that quietly ignored these
    /// would leave the operator with a profile that still does not parse.
    /// </summary>
    private static void AddGuardNotes(List<string> notes, IReadOnlyList<string> lines)
    {
        var totals = lines
            .Where(line => MoneyTokens(line).Count > 0 &&
                           line.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("SUB", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("VAT", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("TAX", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("DISCOUNT", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("ROUNDING", StringComparison.OrdinalIgnoreCase) &&
                           !line.Contains("BALANCE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (totals.Count == 0)
        {
            var otherWord = lines
                .Select((line, index) => (line, index))
                .FirstOrDefault(entry => MoneyTokens(entry.line).Count > 0 &&
                                         (entry.line.Contains("DUE", StringComparison.OrdinalIgnoreCase) ||
                                          entry.line.Contains("RECEIVED", StringComparison.OrdinalIgnoreCase) ||
                                          entry.line.Contains("SETTLED", StringComparison.OrdinalIgnoreCase)));

            if (otherWord.line is not null)
            {
                notes.Add(
                    "The words that announce the printed total are fixed in the parser (a line must read \"Total\", " +
                    "\"Grand Total\", \"Total:\" or \"Total =\"). This document has no such line; the amount printed as " +
                    $"\"{otherWord.line.Trim()}\" cannot be captured by a profile.");
            }
        }
    }

    private static bool PrintedTotalMismatch(ParseResult result) =>
        result.Reason is { Length: > 0 } reason
        && reason.Contains("does not match the printed total", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The printed-total guard said once, whatever reading hit it: a capture that quietly ignored it
    /// would leave the operator adjusting header words that were never the problem.
    /// </summary>
    private static void AddMismatchNote(List<string> notes, CaptureReading reading)
    {
        if (reading.IsSuccess || !PrintedTotalMismatch(reading.Result))
        {
            return;
        }

        const string text =
            "The items and the printed total disagree, which no profile can fix: a discount or a charge that is " +
            "not on an item line is not supported, and the receipt is held rather than fiscalised on a guess.";
        if (!notes.Contains(text, StringComparer.Ordinal))
        {
            notes.Add(text);
        }
    }

    private static IEnumerable<(string Name, string Quantity, string Total, int Index)> HeaderCandidates(
        IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var collapsed = TaxMarkerReader.Collapse(lines[index]);
            if (collapsed.Length == 0)
            {
                continue;
            }

            var name = NameColumns.FirstOrDefault(word => collapsed.Contains(word, StringComparison.Ordinal));
            var quantity = QuantityColumns.FirstOrDefault(word => collapsed.Contains(word, StringComparison.Ordinal));
            var total = TotalColumns.FirstOrDefault(word => collapsed.Contains(word, StringComparison.Ordinal));

            if (name is null || quantity is null || total is null)
            {
                continue;
            }

            if (!HasItemRowsBelow(lines, index))
            {
                continue;
            }

            yield return (name, quantity, total, index);
        }
    }

    private static bool ReadsAsHeader(string collapsed, ParseConfig current)
    {
        var required = current.ItemTableHeaderRequires.Select(TaxMarkerReader.Collapse).ToArray();
        var anyOf = current.ItemTableHeaderAnyOf.Select(TaxMarkerReader.Collapse).ToArray();
        return required.Length > 0 && anyOf.Length > 0 &&
               required.All(word => collapsed.Contains(word, StringComparison.Ordinal)) &&
               anyOf.Any(word => collapsed.Contains(word, StringComparison.Ordinal));
    }

    private static bool IsCurrentEndMarker(string marker, ParseConfig current) =>
        current.ItemTableEndMarkers.Any(entry =>
            !string.IsNullOrWhiteSpace(entry) &&
            marker.StartsWith(entry.Trim().ToLowerInvariant(), StringComparison.Ordinal));

    private static ParseConfig LayoutClone(ParseConfig current)
    {
        var clone = new ParseConfig { Profile = current.Profile };
        current.ExportRules().ApplyTo(clone);
        clone.Currency = current.Currency;
        clone.Currencies = current.Currencies;
        clone.PaymentMethod = current.PaymentMethod;
        clone.VatFlag = current.VatFlag;
        clone.VatRate = current.VatRate;
        clone.TaxSymbol = current.TaxSymbol;
        clone.NonTaxSymbol = current.NonTaxSymbol;
        clone.CustomerEmail = current.CustomerEmail;
        return clone;
    }

    private static int IndexOf(IReadOnlyList<string> lines, Func<string, bool> matches)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (matches(lines[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasItemRowsBelow(IReadOnlyList<string> lines, int headerIndex)
    {
        for (var index = headerIndex + 1; index < lines.Count; index++)
        {
            if (AmountTokens(lines[index]).Count >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static int FirstItemLine(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (AmountTokens(lines[index]).Count >= 2)
            {
                return index;
            }
        }

        return 0;
    }

    private static bool LooksLikeItemRow(string line) => AmountTokens(line).Count >= 2;

    /// <summary>
    /// The line printed directly above the items, when it is a short label rather than another
    /// value. A block marker several lines up is a guess about the layout, so it is not offered.
    /// </summary>
    private static (string Line, int Index)? StartMarkerFor(IReadOnlyList<string> lines, int firstItem)
    {
        for (var index = firstItem - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Any(char.IsDigit) || SplitTokens(line).Length > 4)
            {
                return null;
            }

            return (line, index);
        }

        return null;
    }

    /// <summary>
    /// The first line after the items that opens the footer. The marker is its words only, up to the
    /// amount: a marker that carries this receipt's total would stop matching the next one.
    /// </summary>
    private static (string Marker, string Line, int Index)? EndMarkerAfter(IReadOnlyList<string> lines, int from)
    {
        for (var index = Math.Max(from, 0) + 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lower = line.ToLowerInvariant();
            if (!FooterWords.Any(word => lower.StartsWith(word, StringComparison.Ordinal)))
            {
                continue;
            }

            var words = new List<string>();
            foreach (var token in SplitTokens(line))
            {
                if (IsMoney(token))
                {
                    break;
                }

                words.Add(token);
                if (words.Count == 4)
                {
                    break;
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            return (string.Join(' ', words).ToLowerInvariant(), string.Join(' ', words), index);
        }

        return null;
    }

    private static bool Appears(IReadOnlyList<string> lines, string? marker) =>
        !string.IsNullOrWhiteSpace(marker) &&
        lines.Any(line => line.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the marker the parser would search for lands on the line it was taken from. The item
    /// block is bounded by the first line that <em>contains</em> each marker, so a marker naming a
    /// column as well as a footer — "Total" in the table header and again above the amounts — bounds
    /// the block at the header instead and turns the receipt's own totals into candidate item lines.
    /// Such a marker is not offered; the till keeps looking for the one it already has.
    /// </summary>
    private static bool UsableMarker(IReadOnlyList<string> lines, string marker, int intendedIndex) =>
        IndexOf(lines, line => line.Contains(marker, StringComparison.OrdinalIgnoreCase)) == intendedIndex;

    /// <summary>The text before the first ':' (or a column gap), and what is printed after it.</summary>
    private static (string? Label, string? Value) LabelAndValue(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0)
        {
            return (null, null);
        }

        var index = line.IndexOf(':');
        var skip = 1;
        if (index < 0)
        {
            var gap = Regex.Match(line, @"^(?<label>\S(?:.*?\S)?)(?=\s{2,}\S)", RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (!gap.Success)
            {
                return (null, null);
            }

            index = gap.Groups["label"].Value.Length;
            skip = 0;
        }

        var label = CleanLabel(line[..index]);
        var value = line[(index + skip)..].Trim(':', '=', '-', '|', ' ').Trim();
        return label is null || value.Length == 0 ? (null, null) : (label, value);
    }

    private static string? CleanLabel(string text)
    {
        var label = text.Trim().TrimEnd(':', '=', '-', '|').Trim();
        var words = SplitTokens(label);
        if (words.Length is < 1 or > 4 || !label.Any(char.IsLetter) || label.Any(char.IsDigit))
        {
            return null;
        }

        return label;
    }

    /// <summary>
    /// A value the parser can carry as a document number. A decimal amount is not one — but a till that
    /// prints "SLIP NO: 4471" does print a number with no letters in it, and the parser accepts it, so
    /// the capture has to offer the label too.
    /// </summary>
    private static bool IsDocumentNumber(string value) =>
        value.Length is >= 3 and <= 40 &&
        !LooksLikeAmount(value) &&
        Regex.IsMatch(
            value,
            @"^[A-Za-z0-9][A-Za-z0-9\-/\.]*\d[A-Za-z0-9\-/\.]*$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));

    private static bool LooksLikeAmount(string token) =>
        token.Contains('.') && ReceiptParser.TryParseMoney(token, out _);

    private static bool IsPrintedDateReadable(string printed, List<string> formats)
    {
        var token = printed.Trim();
        return DateTime.TryParseExact(
                   token,
                   formats.ToArray(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out _) ||
               DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>
    /// The formats that describe how the POS wrote this date. Which of the two short parts is the day
    /// is not knowable from the paper, so both orders are offered and the preview shows which one the
    /// till reads; a date that is ambiguous on the receipt is ambiguous in the profile too.
    /// </summary>
    private static List<string> DerivedFormats(string printed)
    {
        var separator = printed.FirstOrDefault(character => !char.IsDigit(character) && character != ' ');
        var glue = separator == 0 ? "/" : separator.ToString();
        var time = Regex.IsMatch(printed, @"\d{1,2}:\d{2}") ? " HH:mm" : "";

        if (Regex.IsMatch(printed, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}"))
        {
            return new List<string> { "yyyy" + glue + "MM" + glue + "dd" + time };
        }

        var year = Regex.IsMatch(printed, @"\d{4}$") ? "yyyy" : "yy";
        return new List<string>
        {
            "dd" + glue + "MM" + glue + year + time,
            "MM" + glue + "dd" + glue + year + time
        };
    }

    private static CorrectionVocabulary Clone(CorrectionVocabulary source) => new()
    {
        CreditNoteKeywords = source.CreditNoteKeywords.ToList(),
        DebitNoteKeywords = source.DebitNoteKeywords.ToList(),
        CreditNoteNumberPrefixes = source.CreditNoteNumberPrefixes.ToList(),
        OriginalInvoiceLabels = source.OriginalInvoiceLabels.ToList(),
        ReasonLabels = source.ReasonLabels.ToList(),
        NegativeTotalMeansCreditNote = source.NegativeTotalMeansCreditNote
    };

    private static List<string> Append(List<string> current, string addition) =>
        Append(current, new[] { addition });

    private static List<string> Append(List<string> current, IEnumerable<string> additions)
    {
        var values = current.ToList();
        foreach (var addition in additions)
        {
            if (!string.IsNullOrWhiteSpace(addition) &&
                !values.Any(entry => string.Equals(entry.Trim(), addition.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(addition.Trim());
            }
        }

        return values;
    }

    /// <summary>
    /// True when either phrase's words appear in the other, ignoring the punctuation a till roll
    /// puts between a label and its value ("Buyer VAT" and "VAT No." are the same label to the
    /// parser, which matches a printed line for containing the keyword).
    /// </summary>
    private static bool SharesWords(string entry, string printed)
    {
        var left = Norm(entry);
        var right = Norm(printed);
        return left.Length == 0 || right.Length == 0
            ? false
            : right.Contains(left, StringComparison.Ordinal) || left.Contains(right, StringComparison.Ordinal);
    }

    private static string Norm(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static bool WholeWord(string word, string line) =>
        Regex.IsMatch(
            line,
            @"\b" + string.Join(@"\s+", SplitTokens(word).Select(Regex.Escape)) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));

    private static bool AnnouncesCorrection(IReadOnlyList<string> lines, ParseConfig current)
    {
        var firstItem = Math.Max(FirstItemLine(lines), 1);
        for (var index = 0; index < firstItem && index < lines.Count; index++)
        {
            if (current.Corrections.CreditNoteKeywords.Any(keyword => WholeWord(keyword, lines[index])) ||
                current.Corrections.DebitNoteKeywords.Any(keyword => WholeWord(keyword, lines[index])))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> MoneyTokens(string line) => SplitTokens(line).Where(IsMoney).ToList();

    /// <summary>
    /// The tokens written like an amount. A phone number printed in groups ("0772 555 111") is a run of
    /// plain integers and an item line is not, so this is what keeps the two apart when the capture
    /// looks for the lines the parser reads as items.
    /// </summary>
    private static List<string> AmountTokens(string line) => SplitTokens(line)
        .Where(token => (token.Contains('.') || token.Contains(',')) && IsMoney(token))
        .ToList();

    private static string[] SplitTokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsMoney(string token) =>
        ReceiptParser.TryParseMoney(token, out var value) && Math.Abs(value) > 0.0001m;

    private static IReadOnlyList<string> Split(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static int LineOf(IReadOnlyList<string> lines, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static int DateLine(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (DatePattern.IsMatch(lines[index]))
            {
                return index + 1;
            }
        }

        return 0;
    }
}
