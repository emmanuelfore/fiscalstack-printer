using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PrintInterceptor.Core.Configuration;

namespace PrintInterceptor.Core.Parsing;

/// <summary>
/// Marker/column parser for POS receipts, ported from Interface/ReadFile.cs.
/// It locates quantity/price/amount columns by validating qty * price = amount,
/// then fails closed when no items or the printed total disagrees with the items.
/// </summary>
public sealed class ReceiptParser
{
    /// <summary>How many trailing short tokens may be read as tax codes, e.g. "2.50 EX".</summary>
    private const int MaxTaxCodesPerLine = 3;

    private const int MaxTaxCodeLength = 4;

    private readonly ParseConfig _config;
    private readonly Regex _emailPattern;
    private readonly Regex _invoiceNumberPattern;
    private readonly Regex _invoiceRowPattern;
    private readonly Regex _currencyBeforeAmountPattern;
    private readonly Regex _buyerLabelPattern;
    private readonly Regex _amountLinePattern;
    private readonly Regex _twoLineQuantityPattern;
    private readonly Regex _twoLineNameAmountPattern;
    private readonly string[] _dateFormats;
    private readonly TaxMarkerReader _tax;

    public ReceiptParser(ParseConfig config)
    {
        _config = config;
        _tax = new TaxMarkerReader(config);

        var timeout = TimeSpan.FromMilliseconds(config.Limits.RegexTimeoutMilliseconds);
        var patterns = config.Patterns;
        _dateFormats = config.DateFormats.ToArray();
        _emailPattern = Compile(patterns.Email, timeout, RegexOptions.None);
        _invoiceNumberPattern = Compile(patterns.InvoiceNumber, timeout, RegexOptions.None);
        _invoiceRowPattern = Compile(patterns.InvoiceRow, timeout, RegexOptions.IgnoreCase);
        _currencyBeforeAmountPattern = Compile(patterns.CurrencyBeforeAmount, timeout, RegexOptions.None);
        _buyerLabelPattern = Compile(patterns.BuyerLabel, timeout, RegexOptions.IgnoreCase);
        _amountLinePattern = Compile(patterns.AmountLine, timeout, RegexOptions.IgnoreCase);
        _twoLineQuantityPattern = Compile(patterns.TwoLineQuantity, timeout, RegexOptions.None);
        _twoLineNameAmountPattern = Compile(patterns.TwoLineNameAmount, timeout, RegexOptions.None);
    }

    /// <summary>
    /// A profile can carry any regex, and the JIT compiler rejects a few that the
    /// interpreter accepts. Falling back to the interpreter keeps a hand-edited
    /// profile working instead of taking the till down for a pattern quirk.
    /// </summary>
    private static Regex Compile(string pattern, TimeSpan timeout, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, timeout);
        }
        catch (ArgumentException)
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant, timeout);
        }
    }

    private int MinItemTokens => _config.Limits.MinItemTokens;

    private decimal LineTolerance => _config.Limits.LineTolerance;

    /// <summary>
    /// The absolute test is used on its own for a printed total: a document-level
    /// disagreement means an unmodelled discount or charge, so it must stay exact.
    /// Line arithmetic additionally allows a relative error, because a POS rounds
    /// the unit price it prints and a heavily rounded line still has to parse.
    /// </summary>
    private bool WithinTolerance(decimal expected, decimal actual)
    {
        var limits = _config.Limits;
        var error = Math.Abs(expected - actual);
        return error <= limits.LineTolerance ||
               (actual != 0 && error / Math.Max(actual, expected) <= limits.RelativeTolerance);
    }

    public ParseResult Parse(string text)
    {
        try
        {
            return ParseText(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            // A profile pattern backtracked past its per-match budget. The job is held
            // for review instead of taking the capture thread down with it.
            return ParseResult.Review(
                $"A parse pattern from profile '{_config.Profile}' timed out on this job " +
                $"({ex.Pattern?[..Math.Min(ex.Pattern.Length, 40)]}…). Nothing was fiscalised; " +
                "narrow the pattern or raise parse.limits.regexTimeoutMilliseconds.");
        }
    }

    private ParseResult ParseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ParseResult.Review("The captured job is empty.");
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var warnings = new List<string>();

        var invoiceTable = TryParseInvoiceTable(lines, text);
        if (invoiceTable is not null)
        {
            return invoiceTable;
        }

        var (start, end) = ResolveItemBlock(lines);
        if (end <= start)
        {
            return ParseResult.Review("No line items could be parsed. The job was not fiscalised.", warnings);
        }

        var strategy = DetectStrategy(lines, start, end);
        if (strategy is not null && strategy.Confidence < _config.Limits.MinConfidence)
        {
            warnings.Add($"Low column confidence ({strategy.Confidence:P0}); verify the parsed items.");
        }

        var items = new List<ParsedItem>();
        var taxDecisions = new List<TaxBucket>();
        decimal total = 0;
        decimal taxTotal = 0;

        if (strategy is not null)
        {
            string? sectionTaxType = null;
            for (var i = start; i < end && i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var columnItem = TryReadColumnItem(line, strategy);
                if (columnItem is null)
                {
                    // A line that is not an item may be the header that names the lines
                    // below it: "ZERO RATED ITEMS", "EXEMPT SUPPLIES".
                    sectionTaxType = _tax.ReadSection(line) ?? sectionTaxType;
                    continue;
                }

                var bucket = ResolveTaxBucket(line, columnItem.Slot, null, sectionTaxType);
                items.Add(CreateItem(columnItem.Name, columnItem.Quantity, columnItem.UnitPrice, columnItem.Amount, bucket, out var lineTax));
                taxDecisions.Add(bucket);
                total += columnItem.Amount;
                taxTotal += lineTax;
            }
        }

        // Fiscal registers often wrap an item over two lines: "price x qty"
        // followed by "name *subtotal". Only used when the single-line
        // column layout produced nothing, so the formats never mix.
        if (items.Count == 0)
        {
            ParseTwoLineItems(lines, start, end, items, taxDecisions, ref total, ref taxTotal, warnings);
        }

        if (items.Count == 0)
        {
            return ParseResult.Review(
                strategy is null
                    ? "Could not identify quantity/price/amount columns in the receipt."
                    : "No line items could be parsed. The job was not fiscalised.",
                warnings);
        }

        if (total <= 0)
        {
            return ParseResult.Review("The parsed item total is not positive.", warnings);
        }

        var printedTotal = ExtractPrintedTotal(lines);
        if (printedTotal is not null && Math.Abs(printedTotal.Value - total) > LineTolerance)
        {
            return ParseResult.Review(
                $"Parsed item total {total:0.00} does not match the printed total {printedTotal.Value:0.00}. " +
                "Discounts or non-item charges are not supported; refusing to fiscalise.",
                warnings);
        }

        ReportTaxDecisions(taxDecisions, warnings);

        var invoiceNumber = ExtractInvoiceNumber(lines);
        if (string.IsNullOrEmpty(invoiceNumber))
        {
            warnings.Add("Invoice number not found; duplicate protection falls back to the exact job hash.");
        }

        var currency = DetectCurrency(lines);
        var paymentMethod = DetectPaymentMethod(lines);
        var (transactionType, relatedInvoice, reason) = DetectTransactionType(lines, invoiceNumber, warnings);

        if (transactionType == "CreditNote" && string.IsNullOrEmpty(relatedInvoice))
        {
            return ParseResult.Review(
                "Credit note has no original invoice number; refusing to fiscalise a correction without a reference.",
                warnings);
        }

        var buyer = BuildBuyer(lines);

        var receipt = new ParsedReceipt
        {
            Items = items,
            InvoiceNumber = invoiceNumber,
            Date = ExtractDate(lines),
            Currency = currency,
            PaymentMethod = paymentMethod,
            TransactionType = transactionType,
            RelatedInvoiceNumber = relatedInvoice,
            CreditNoteReason = reason,
            Buyer = buyer,
            Total = Math.Round(total, 2),
            TaxTotal = Math.Round(taxTotal, 2),
            SourceText = text
        };

        return ParseResult.Success(receipt, warnings);
    }

    private ParsedBuyer BuildBuyer(string[] lines) => new()
    {
        Name = ExtractBuyerName(lines),
        VatNumber = ExtractBuyerVat(lines),
        Tin = ExtractBuyerTin(lines),
        Email = ExtractEmail(lines),
        Phone = ExtractValue(lines, LabelsFor(_config.CustomerTelephoneNumber, _config.PhoneLabels)),
        Address = ExtractValue(lines, LabelsFor(_config.CustomerAddress, _config.AddressLabels))
    };

    /// <summary>The field's own configured label first, then the profile's fallback labels.</summary>
    private static IEnumerable<string> LabelsFor(string configured, IEnumerable<string> fallback) =>
        fallback is null
            ? new[] { configured }
            : new[] { configured }.Concat(fallback);

    private ParseResult? TryParseInvoiceTable(string[] lines, string text)
    {
        var header = FindInvoiceTableHeader(lines);
        if (header < 0)
        {
            return null;
        }

        var items = new List<ParsedItem>();
        var taxDecisions = new List<TaxBucket>();
        var warnings = new List<string>();
        var pendingName = new StringBuilder();
        decimal total = 0;
        decimal taxTotal = 0;
        string? sectionTaxType = null;

        for (var i = header + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsInvoiceTableEnd(line))
            {
                break;
            }

            var match = _invoiceRowPattern.Match(line);
            if (!match.Success)
            {
                // A line the row pattern rejects may still be the header that names the rows
                // below it; either way it is not part of an item's description.
                var section = _tax.ReadSection(line);
                if (section is not null)
                {
                    sectionTaxType = section;
                    pendingName.Clear();
                    continue;
                }

                if (pendingName.Length > 0)
                {
                    pendingName.Append(' ');
                }

                pendingName.Append(line);
                continue;
            }

            if (!TryParseInvoiceRow(
                    match,
                    pendingName.ToString(),
                    sectionTaxType,
                    out var item,
                    out var bucket,
                    out var lineTax,
                    out var rowError))
            {
                warnings.Add(rowError);
                continue;
            }

            items.Add(item);
            taxDecisions.Add(bucket);
            total += item.Amount;
            taxTotal += lineTax;
            pendingName.Clear();
        }

        if (items.Count == 0)
        {
            return null;
        }

        total = Math.Round(total, 2);
        taxTotal = Math.Round(taxTotal, 2);

        var printedTotal = ExtractPrintedTotal(lines);
        if (printedTotal is not null && Math.Abs(printedTotal.Value - total) > LineTolerance)
        {
            return ParseResult.Review(
                $"Parsed invoice item total {total:0.00} does not match the printed total {printedTotal.Value:0.00}. " +
                "The invoice table was not fiscalised.",
                warnings);
        }

        ReportTaxDecisions(taxDecisions, warnings);

        return BuildParsedResult(lines, text, items, total, taxTotal, warnings);
    }

    private int FindInvoiceTableHeader(string[] lines)
    {
        // Profiles are hand-edited, so "Excl. Total" and "EXCLTOTAL" name the same column.
        var required = _config.ItemTableHeaderRequires.Select(CollapseInvoiceHeader).ToArray();
        var anyOf = _config.ItemTableHeaderAnyOf.Select(CollapseInvoiceHeader).ToArray();
        if (required.Length == 0 || anyOf.Length == 0)
        {
            return -1;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var normalized = CollapseInvoiceHeader(lines[i]);
            if (required.All(token => normalized.Contains(token, StringComparison.Ordinal)) &&
                anyOf.Any(token => normalized.Contains(token, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string CollapseInvoiceHeader(string line) => TaxMarkerReader.Collapse(line);

    private bool IsInvoiceTableEnd(string line)
    {
        var lower = line.Trim().ToLowerInvariant();
        return _config.ItemTableEndMarkers.Any(marker =>
            !string.IsNullOrWhiteSpace(marker) &&
            lower.StartsWith(marker.Trim().ToLowerInvariant(), StringComparison.Ordinal));
    }

    private bool TryParseInvoiceRow(
        Match match,
        string pendingName,
        string? sectionTaxType,
        out ParsedItem item,
        out TaxBucket bucket,
        out decimal lineTax,
        out string error)
    {
        item = null!;
        lineTax = 0;
        error = "";
        bucket = new TaxBucket(_tax.DefaultTaxType, TaxFromDefault, null, null, null);

        if (!TryParseMoney(match.Groups["qty"].Value, out var quantity) || quantity <= 0)
        {
            error = "Invoice row has an invalid quantity.";
            return false;
        }

        if (!TryParseMoney(match.Groups["price"].Value, out var unitPrice) || unitPrice < 0 ||
            !TryParseMoney(match.Groups["disc"].Value, out var discountPercent) ||
            !TryParseMoney(match.Groups["vat"].Value, out var vatPercent) ||
            !TryParseMoney(match.Groups["exclusive"].Value, out var exclusiveTotal))
        {
            error = "Invoice row has invalid price, discount, VAT or amount columns.";
            return false;
        }

        var hasInclusive = match.Groups["inclusive"].Success;
        var inclusiveTotal = 0m;
        if (hasInclusive && !TryParseMoney(match.Groups["inclusive"].Value, out inclusiveTotal))
        {
            error = "Invoice row has an invalid inclusive total.";
            return false;
        }

        var discount = Math.Clamp(discountPercent / 100m, 0m, 1m);
        var vatRate = Math.Max(0m, vatPercent / 100m);
        var expectedExclusive = Math.Round(quantity * unitPrice * (1m - discount), 2);
        if (Math.Abs(expectedExclusive - exclusiveTotal) > LineTolerance)
        {
            error = $"Invoice row qty x price ({expectedExclusive:0.00}) does not match its exclusive total ({exclusiveTotal:0.00}).";
            return false;
        }

        var amount = hasInclusive ? inclusiveTotal : Math.Round(exclusiveTotal * (1m + vatRate), 2);
        if (hasInclusive)
        {
            var expectedInclusive = Math.Round(exclusiveTotal * (1m + vatRate), 2);
            if (Math.Abs(expectedInclusive - amount) > LineTolerance)
            {
                error = $"Invoice row exclusive total ({exclusiveTotal:0.00}) plus VAT ({vatPercent:0.##}%) does not match its inclusive total ({amount:0.00}).";
                return false;
            }
        }

        lineTax = Math.Round(amount - exclusiveTotal, 2);
        var name = CombineInvoiceItemName(pendingName, match.Groups["name"].Value);
        if (name.Length == 0)
        {
            name = "Item";
        }

        bucket = ResolveTaxBucket(
            $"{pendingName} {match.Value}",
            TaxRowSlotTokens(match),
            vatRate,
            sectionTaxType);

        // The row's own columns stay the source of the money; the bucket only decides which
        // ZIMRA tax ID the gateway files it under.
        var itemRate = bucket.TaxType == ParseConfig.TaxTypeStandard
            ? (vatRate > 0 ? vatRate : (decimal)_config.VatRate)
            : 0m;

        item = new ParsedItem(name, quantity, amount / quantity, Math.Round(amount, 2), bucket.TaxType, itemRate);
        return true;
    }

    /// <summary>The invoice row's own tax column, when the profile's pattern exposes one.</summary>
    private static IReadOnlyList<string> TaxRowSlotTokens(Match match)
    {
        var group = match.Groups["tax"];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value)
            ? new[] { group.Value.Trim() }
            : Array.Empty<string>();
    }

    private static string CombineInvoiceItemName(string pendingName, string inlineName)
    {
        var name = $"{pendingName} {inlineName}".Trim();
        name = Regex.Replace(name, @"\s+", " ");
        return name.Trim(' ', '-', ':', '|', '*', '.');
    }

    private ParseResult BuildParsedResult(
        string[] lines,
        string text,
        IReadOnlyList<ParsedItem> items,
        decimal total,
        decimal taxTotal,
        List<string> warnings)
    {
        var invoiceNumber = ExtractInvoiceNumber(lines);
        if (string.IsNullOrEmpty(invoiceNumber))
        {
            warnings.Add("Invoice number not found; duplicate protection falls back to the exact job hash.");
        }

        var (transactionType, relatedInvoice, reason) = DetectTransactionType(lines, invoiceNumber, warnings);
        if (transactionType == "CreditNote" && string.IsNullOrEmpty(relatedInvoice))
        {
            return ParseResult.Review(
                "Credit note has no original invoice number; refusing to fiscalise a correction without a reference.",
                warnings);
        }

        var receipt = new ParsedReceipt
        {
            Items = items,
            InvoiceNumber = invoiceNumber,
            Date = ExtractDate(lines),
            Currency = DetectCurrency(lines),
            PaymentMethod = DetectPaymentMethod(lines),
            TransactionType = transactionType,
            RelatedInvoiceNumber = relatedInvoice,
            CreditNoteReason = reason,
            Buyer = BuildBuyer(lines),
            Total = Math.Round(total, 2),
            TaxTotal = Math.Round(taxTotal, 2),
            SourceText = text
        };

        return ParseResult.Success(receipt, warnings);
    }

    private (int Start, int End) ResolveItemBlock(string[] lines)
    {
        var startMarker = _config.ProductStartLine;
        var endMarker = _config.ProductEndLine;

        var startIndex = FindLine(lines, startMarker, 0);
        var endIndex = FindLine(lines, endMarker, 0);

        if (startIndex >= 0 && endIndex > startIndex)
        {
            return (startIndex + 1, endIndex);
        }

        if (startIndex >= 0)
        {
            return (startIndex + 1, lines.Length);
        }

        if (endIndex >= 0)
        {
            return (0, endIndex);
        }

        // No markers found: let column validation filter the whole job.
        return (0, lines.Length);
    }

    private ColumnStrategy? DetectStrategy(string[] lines, int start, int end)
    {
        var candidates = new List<ColumnStrategy>();

        for (var fromRight = 0; fromRight <= 1; fromRight++)
        {
            for (var q = 0; q < 3; q++)
            for (var p = 0; p < 3; p++)
            for (var a = 0; a < 3; a++)
            {
                if (q == p || q == a || p == a)
                {
                    continue;
                }

                var strategy = new ColumnStrategy(fromRight == 1, q, p, a);
                var attempts = 0;
                var valid = 0;
                decimal totalError = 0;

                for (var i = start; i < end && i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || IsNonItemLine(line))
                    {
                        continue;
                    }

                    var tokens = SplitItemTokens(line).Tokens;
                    var indices = strategy.Resolve(tokens.Length, MinItemTokens);
                    if (indices is null)
                    {
                        continue;
                    }

                    if (!TryParseMoney(tokens[indices.Value.Qty], out var quantity) ||
                        !TryParseMoney(tokens[indices.Value.Price], out var price) ||
                        !TryParseMoney(tokens[indices.Value.Amount], out var amount) ||
                        quantity <= 0)
                    {
                        continue;
                    }

                    attempts++;
                    var expected = Math.Round(quantity * price, 2);

                    if (WithinTolerance(expected, amount))
                    {
                        valid++;
                        totalError += Math.Abs(expected - amount);
                    }
                }

                if (attempts == 0 || valid == 0)
                {
                    continue;
                }

                strategy.Score = valid;
                var confidence = (double)valid / attempts;
                var averageError = (double)(totalError / valid);
                confidence *= 1.0 - Math.Min(averageError / 10.0, 0.5);
                strategy.Confidence = confidence;
                candidates.Add(strategy);
            }
        }

        return candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.IsMonotonic)
            .ThenByDescending(c => c.Score)
            .FirstOrDefault();
    }

    private decimal CalculateLineTax(decimal amount, decimal rate)
    {
        if (rate <= 0)
        {
            return 0;
        }

        if (IsVatInclusive)
        {
            return Math.Round(amount - amount / (1 + rate), 2);
        }

        return Math.Round(amount * rate, 2);
    }

    private bool IsVatInclusive => _config.VatFlag.Trim() == "1";

    /// <summary>
    /// The VAT bucket chosen for one line, how it was chosen — which is what turns "the
    /// receipt did not say" into a warning an operator can act on — and the rate the row
    /// printed, when the paper stated one.
    /// </summary>
    private sealed record TaxBucket(
        string TaxType,
        string Source,
        string? Marker,
        string? ConflictsWith,
        decimal? PrintedRate);

    private const string TaxFromMarker = "marker";
    private const string TaxFromSection = "section";
    private const string TaxFromPrintedRate = "printed rate";
    private const string TaxFromDefault = "default";

    /// <summary>
    /// The bucket for one item line: a marker on the line itself, then the header naming the
    /// block it sits in, then the VAT percent the row printed, then parse.defaultTaxType.
    /// Nothing here can hold a job — a sale is never blocked because its tax status is
    /// unclear, because the till has to keep serving.
    /// </summary>
    private TaxBucket ResolveTaxBucket(
        string line,
        IReadOnlyList<string> slotTokens,
        decimal? printedRate,
        string? sectionTaxType)
    {
        var marker = _tax.Read(line, slotTokens);
        if (marker is not null)
        {
            return new TaxBucket(marker.TaxType, TaxFromMarker, marker.Marker, marker.ConflictsWith, printedRate);
        }

        if (sectionTaxType is not null)
        {
            return new TaxBucket(sectionTaxType, TaxFromSection, null, null, printedRate);
        }

        if (printedRate is { } rate)
        {
            return new TaxBucket(
                rate > 0 ? ParseConfig.TaxTypeStandard : ParseConfig.TaxTypeZeroRated,
                TaxFromPrintedRate,
                null,
                null,
                rate);
        }

        // With nothing on the paper to read, the shop's own configuration speaks: a business
        // with no VAT rate configured is not charging STANDARD on anything.
        if (_tax.DefaultTaxType == ParseConfig.TaxTypeStandard && _config.VatRate <= 0)
        {
            return new TaxBucket(ParseConfig.TaxTypeZeroRated, TaxFromDefault, null, null, null);
        }

        return new TaxBucket(_tax.DefaultTaxType, TaxFromDefault, null, null, null);
    }

    /// <summary>
    /// Reports what the markers could not settle. A POS that prints no per-line tax statement
    /// is not a fault, so that case stays quiet; a receipt where only part of the lines
    /// declared theirs is how a code missing from the profile shows up.
    /// </summary>
    private void ReportTaxDecisions(IReadOnlyList<TaxBucket> decisions, List<string> warnings)
    {
        foreach (var conflict in decisions.Where(decision => decision.ConflictsWith is not null).Take(3))
        {
            warnings.Add(
                $"A line declares {conflict.TaxType} ('{conflict.Marker}') and {conflict.ConflictsWith}; " +
                $"fiscalised as {conflict.TaxType}. Remove whichever marker this POS does not print.");
        }

        foreach (var contradiction in decisions
                     .Where(decision => decision.Source == TaxFromMarker && ContradictsPrintedRate(decision))
                     .Take(3))
        {
            warnings.Add(
                $"A line declares {contradiction.TaxType} ('{contradiction.Marker}') but prints " +
                $"{contradiction.PrintedRate!.Value:P1} VAT; fiscalised as {contradiction.TaxType}.");
        }

        var unmarked = decisions.Count(decision => decision.Source == TaxFromDefault);
        if (unmarked > 0 && unmarked < decisions.Count)
        {
            warnings.Add(
                $"{unmarked} of {decisions.Count} item lines print no tax marker; fiscalised as " +
                $"{_tax.DefaultTaxType}. Add this POS's tax codes to parse.taxRules if some of them are " +
                "zero-rated or exempt.");
        }

        var zeroRatedByRate = decisions.Count(decision =>
            decision.Source == TaxFromPrintedRate && decision.TaxType == ParseConfig.TaxTypeZeroRated);
        if (zeroRatedByRate > 0)
        {
            warnings.Add(
                $"{zeroRatedByRate} line(s) print 0% VAT with no tax code, read as ZERO_RATED. A 0% figure " +
                "cannot tell a zero-rated supply from an exempt one — if this POS sells exempt items, " +
                "add the code it prints to parse.taxRules.");
        }
    }

    /// <summary>
    /// A 0% figure is not a claim of exemption, so it only contradicts a marker that says the
    /// line is taxable.
    /// </summary>
    private static bool ContradictsPrintedRate(TaxBucket decision) =>
        decision.PrintedRate is { } rate &&
        (decision.TaxType == ParseConfig.TaxTypeStandard) != (rate > 0);

    private ParsedItem CreateItem(
        string name,
        decimal quantity,
        decimal unitPrice,
        decimal amount,
        TaxBucket bucket,
        out decimal lineTax)
    {
        // Only STANDARD carries a rate. The gateway charges the company rate behind the API
        // key; this rate is used for the tax shown on the till's own copy.
        var taxRate = bucket.TaxType == ParseConfig.TaxTypeStandard ? (decimal)_config.VatRate : 0m;

        lineTax = CalculateLineTax(amount, taxRate);
        return new ParsedItem(name, quantity, unitPrice, amount, bucket.TaxType, taxRate);
    }

    private void ParseTwoLineItems(
        string[] lines,
        int start,
        int end,
        List<ParsedItem> items,
        List<TaxBucket> taxDecisions,
        ref decimal total,
        ref decimal taxTotal,
        List<string> warnings)
    {
        var candidates = 0;
        string? sectionTaxType = null;

        for (var i = start; i < end - 1 && i < lines.Length - 1; i++)
        {
            var quantityLine = lines[i].Trim();
            var quantityMatch = _twoLineQuantityPattern.Match(quantityLine);
            if (!quantityMatch.Success)
            {
                sectionTaxType = _tax.ReadSection(quantityLine) ?? sectionTaxType;
                continue;
            }

            var nameLine = lines[i + 1].Trim();
            if (nameLine.Length == 0 || IsNonItemLine(nameLine))
            {
                sectionTaxType = _tax.ReadSection(nameLine) ?? sectionTaxType;
                continue;
            }

            var nameMatch = _twoLineNameAmountPattern.Match(nameLine);
            if (!nameMatch.Success)
            {
                sectionTaxType = _tax.ReadSection(nameLine) ?? sectionTaxType;
                continue;
            }

            var name = nameMatch.Groups["name"].Value
                .Trim(' ', '-', ':', '|', '*', '.')
                .Trim();

            if (name.Length == 0 || !name.Any(char.IsLetter))
            {
                continue;
            }

            if (!TryParseMoney(quantityMatch.Groups["price"].Value, out var unitPrice) ||
                !TryParseMoney(quantityMatch.Groups["qty"].Value, out var quantity) ||
                !TryParseMoney(nameMatch.Groups["amount"].Value, out var amount))
            {
                continue;
            }

            if (quantity <= 0 || unitPrice < 0 || amount < 0)
            {
                continue;
            }

            candidates++;
            var expected = Math.Round(unitPrice * quantity, 2);
            if (!WithinTolerance(expected, amount))
            {
                continue;
            }

            // A register wraps the item over two lines and leaves no column for a tax code,
            // so only a bracketed note or the header above the block can state its status.
            var bucket = ResolveTaxBucket($"{quantityLine} {nameLine}", Array.Empty<string>(), null, sectionTaxType);
            items.Add(CreateItem(name, quantity, unitPrice, amount, bucket, out var lineTax));
            taxDecisions.Add(bucket);
            total += amount;
            taxTotal += lineTax;
            i++; // the name line belongs to this item
        }

        if (candidates > 0 && items.Count == 0)
        {
            warnings.Add("Two-line items were found but qty × price did not match the printed amounts.");
        }
    }

    /// <summary>An item line read through the winning column layout.</summary>
    private sealed record ColumnItem(
        string Name,
        decimal Quantity,
        decimal UnitPrice,
        decimal Amount,
        IReadOnlyList<string> Slot);

    /// <summary>
    /// Reads one line as an item, or returns null when it is not one. Splitting this out of
    /// the loop lets the caller treat a rejected line as a possible section header instead of
    /// losing the statement it makes about the lines below it.
    /// </summary>
    private ColumnItem? TryReadColumnItem(string line, ColumnStrategy strategy)
    {
        if (IsNonItemLine(line))
        {
            return null;
        }

        var (tokens, trailing) = SplitItemTokens(line);
        if (tokens.Length < MinItemTokens)
        {
            return null;
        }

        var indices = strategy.Resolve(tokens.Length, MinItemTokens);
        if (indices is null)
        {
            return null;
        }

        if (!TryParseMoney(tokens[indices.Value.Qty], out var quantity) ||
            !TryParseMoney(tokens[indices.Value.Price], out var unitPrice) ||
            !TryParseMoney(tokens[indices.Value.Amount], out var amount))
        {
            return null;
        }

        if (quantity <= 0 || amount < 0)
        {
            return null;
        }

        var name = BuildItemName(tokens, indices.Value);
        if (name.Length == 0)
        {
            name = "Item";
        }

        return new ColumnItem(
            name,
            quantity,
            unitPrice,
            amount,
            trailing.Concat(TaxMarkerReader.SlotTokens(tokens, indices.Value.Amount, _config)).ToList());
    }

    /// <summary>
    /// A tax code printed after the amount ("2.50 EX") shifts every column to its left, which
    /// positional detection then reads as a different layout on every line. Splitting the code
    /// off before scoring keeps a line's columns in the same place for detection and for
    /// parsing, and hands the code back as that line's tax slot. Only a short trailing token
    /// that could not be an amount is taken, so a description keeps its own words.
    /// </summary>
    private (string[] Tokens, IReadOnlyList<string> Slot) SplitItemTokens(string line)
    {
        var tokens = SplitTokens(line).ToList();
        var slot = new List<string>();

        while (tokens.Count > MinItemTokens && slot.Count < MaxTaxCodesPerLine)
        {
            var last = tokens[^1];
            if (last.Length > MaxTaxCodeLength || !last.Any(char.IsLetter) || TryParseMoney(last, out _))
            {
                break;
            }

            slot.Insert(0, last);
            tokens.RemoveAt(tokens.Count - 1);
        }

        return (tokens.ToArray(), slot);
    }

    private static string BuildItemName(string[] tokens, ColumnIndices indices)    {
        var parts = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i == indices.Qty || i == indices.Price || i == indices.Amount)
            {
                continue;
            }

            parts.Add(tokens[i]);
        }

        return string.Join(' ', parts)
            .Trim(' ', '-', ':', '|', '*', '.')
            .Trim();
    }

    private bool IsNonItemLine(string line)
    {
        var lower = line.ToLowerInvariant();
        foreach (var keyword in _config.NonItemKeywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) &&
                lower.Contains(keyword.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return lower.Contains("---", StringComparison.Ordinal) || lower.Contains("===", StringComparison.Ordinal);
    }

    private string? ExtractInvoiceNumber(string[] lines)
    {
        var value = ExtractValue(lines, new[] { _config.InvoiceNumber });
        if (string.IsNullOrEmpty(value))
        {
            foreach (var line in lines)
            {
                var match = _invoiceNumberPattern.Match(line);
                if (match.Success)
                {
                    value = match.Groups["value"].Value;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(value))
        {
            value = ExtractValue(lines, _config.InvoiceNumberKeywords);
        }

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        value = Regex.Replace(value, @"[^A-Za-z0-9\-/]", "").Trim('-', '/');
        return value.Length is 0 || value.Length > _config.Limits.MaxInvoiceNumberLength ? null : value;
    }

    private string? ExtractBuyerName(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (_config.BuyerNameLiterals.Any(literal =>
                    !string.IsNullOrWhiteSpace(literal) &&
                    string.Equals(line, literal.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return line;
            }
        }

        var name = ExtractValue(lines, new[] { _config.CustomerName });
        name = CleanBuyerName(name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        name = ExtractValue(lines, _config.BuyerNameKeywords);
        name = CleanBuyerName(name);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private string? CleanBuyerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = Regex.Replace(value.Trim(), @"^Name\s*[:=\-]?\s*", "", RegexOptions.IgnoreCase);
        if (name.Length == 0 || _buyerLabelPattern.IsMatch(name) || !name.Any(char.IsLetter))
        {
            return null;
        }

        var max = _config.Limits.MaxBuyerNameLength;
        return name.Length > max ? name[..max] : name;
    }

    private string? ExtractBuyerVat(string[] lines)
    {
        var value = ExtractValue(lines, LabelsFor(_config.CustomerVATNumber, _config.VatNumberLabels));
        return CleanTaxId(value);
    }

    private string? ExtractBuyerTin(string[] lines)
    {
        var value = ExtractValue(lines, LabelsFor(_config.CustomerTIN, _config.TinLabels));
        return CleanTaxId(value);
    }

    private string? CleanTaxId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = Regex.Match(
                value,
                @"[A-Za-z0-9\-]{" + _config.Limits.MinIdentifierLength + ",}")
            .Value;
        return cleaned.Length == 0 || cleaned.Length > 40 ? null : cleaned;
    }

    private string? ExtractEmail(string[] lines)
    {
        foreach (var line in lines)
        {
            var match = _emailPattern.Match(line);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string? ExtractValue(string[] lines, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                var index = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var value = line[(index + keyword.Length)..].Trim();
                value = value.TrimStart(':', '=', '-', '|').Trim();

                // "Buyer Name: Acme" and "Buyer Name Acme" both land here.
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private DateTimeOffset? ExtractDate(string[] lines)
    {
        var value = ExtractValue(lines, _config.DateLabels) ?? ExtractValueOnNextLine(lines, _config.DateLabels);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(value, _dateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
            !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return null;
        }

        if (parsed.Year < _config.Limits.ValidYearFrom || parsed.Year > _config.Limits.ValidYearTo)
        {
            return null;
        }

        var local = parsed.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
            : parsed;

        return new DateTimeOffset(local);
    }

    private static string? ExtractValueOnNextLine(string[] lines, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            for (var i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim().TrimEnd(':', '=', '-', '|').Trim();
                if (!string.Equals(line, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = lines[i + 1].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private string DetectCurrency(string[] lines)
    {
        var currencies = _config.Currencies ?? ParseConfig.DefaultCurrencies();
        foreach (var line in lines)
        {
            foreach (var currency in currencies)
            {
                if (!string.IsNullOrWhiteSpace(currency.Keyword) &&
                    line.Contains(currency.Keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeCurrency(currency.Name);
                }
            }
        }

        if (lines.Any(line => _currencyBeforeAmountPattern.IsMatch(line)))
        {
            return NormalizeCurrency("LSL");
        }

        return NormalizeCurrency(_config.Currency);
    }

    private static string NormalizeCurrency(string? currency)
    {
        var value = (currency ?? "USD").Trim().ToUpperInvariant();
        return value switch
        {
            "ZWL" => "ZWG",
            "US$" => "USD",
            "M" or "LSL" => "LSL",
            "USD" or "ZWG" => value,
            _ => "USD"
        };
    }

    private string DetectPaymentMethod(string[] lines)
    {
        var value = ExtractValue(lines, new[] { "Payment", "Paid by", "Paid via", "Tender", "Mode of payment" });
        if (!string.IsNullOrWhiteSpace(value))
        {
            var upper = value.ToUpperInvariant();
            if (upper.Contains("CARD") || upper.Contains("VISA") || upper.Contains("MASTER"))
            {
                return "CARD";
            }

            if (upper.Contains("MOBILE") || upper.Contains("ECOCASH") || upper.Contains("MPESA") ||
                upper.Contains("WALLET") || upper.Contains("AIRTEL"))
            {
                return "MOBILE";
            }

            if (upper.Contains("TRANSFER") || upper.Contains("BANK"))
            {
                return "TRANSFER";
            }

            if (upper.Contains("CASH"))
            {
                return "CASH";
            }
        }

        var text = string.Join('\n', lines).ToUpperInvariant();
        if (text.Contains("CASH CUSTOMER", StringComparison.Ordinal) ||
            text.Contains("CASH SALE", StringComparison.Ordinal))
        {
            return "CASH";
        }

        if (text.Contains("BANKING DETAILS", StringComparison.Ordinal) ||
            text.Contains("ACCOUNT NUMBER", StringComparison.Ordinal))
        {
            return "TRANSFER";
        }

        if (text.Contains("ECOCASH", StringComparison.Ordinal) ||
            text.Contains("MPESA", StringComparison.Ordinal))
        {
            return "MOBILE";
        }

        return NormalizePayment(_config.PaymentMethod);
    }

    private static string NormalizePayment(string? method)
    {
        var value = (method ?? "CASH").Trim().ToUpperInvariant();
        return value is "CASH" or "CARD" or "MOBILE" or "TRANSFER" ? value : "CASH";
    }

    private static (string Type, string? RelatedInvoice, string? Reason) DetectTransactionType(
        string[] lines,
        string? invoiceNumber,
        List<string> warnings)
    {
        var text = string.Join('\n', lines).ToUpperInvariant();
        var type = "FiscalInvoice";

        if (text.Contains("CREDIT NOTE", StringComparison.Ordinal) ||
            text.Contains("REFUND", StringComparison.Ordinal) ||
            (invoiceNumber is not null &&
             (invoiceNumber.StartsWith("CR", StringComparison.OrdinalIgnoreCase) ||
              invoiceNumber.Contains("-CR", StringComparison.OrdinalIgnoreCase))))
        {
            type = "CreditNote";
        }
        else if (text.Contains("DEBIT NOTE", StringComparison.Ordinal))
        {
            type = "DebitNote";
        }

        if (type == "FiscalInvoice")
        {
            return (type, null, null);
        }

        var related = ExtractValue(lines, new[]
        {
            "Original Invoice", "Original Receipt", "Against Invoice", "Reversal of"
        });
        var reason = ExtractValue(lines, new[] { "Reason", "Credit Note Reason" });

        if (string.IsNullOrEmpty(related))
        {
            warnings.Add("Correction document has no original invoice reference.");
        }

        return (type, related, reason);
    }

    private decimal? ExtractPrintedTotal(string[] lines)
    {
        decimal? grand = null;
        decimal? fallback = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var lower = line.ToLowerInvariant();

            if (!lower.Contains("total", StringComparison.Ordinal) ||
                lower.Contains("sub", StringComparison.Ordinal) ||
                lower.Contains("tax", StringComparison.Ordinal) ||
                lower.Contains("vat", StringComparison.Ordinal) ||
                lower.Contains("discount", StringComparison.Ordinal) ||
                lower.Contains("rounding", StringComparison.Ordinal) ||
                lower.Contains("balance", StringComparison.Ordinal))
            {
                continue;
            }

            var value = ExtractAmountFromLineOrNext(lines, i);
            if (value is null)
            {
                continue;
            }

            if (lower.Contains("grand", StringComparison.Ordinal) ||
                lower.StartsWith("total:", StringComparison.Ordinal) ||
                lower.StartsWith("total =", StringComparison.Ordinal) ||
                lower.StartsWith("total ", StringComparison.Ordinal))
            {
                grand = value;
            }
            else
            {
                fallback = value;
            }
        }

        return grand ?? fallback;
    }

    private decimal? ExtractAmountFromLineOrNext(string[] lines, int index)
    {
        var tokens = SplitTokens(lines[index]);
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            if (TryParseMoney(tokens[i], out var value))
            {
                return value;
            }
        }

        if (index + 1 < lines.Length)
        {
            var next = lines[index + 1].Trim();
            if (_amountLinePattern.IsMatch(next) && TryParseMoney(next, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string[] SplitTokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool TryParseMoney(string token, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var cleaned = token.Trim();
        var negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');
        cleaned = Regex.Replace(cleaned, @"[^0-9.,\-]", "").Replace(",", "");

        if (negative && !cleaned.StartsWith('-'))
        {
            cleaned = "-" + cleaned;
        }

        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
    }

    private static int FindLine(string[] lines, string? marker, int fallback)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return fallback;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fallback;
    }

    private readonly record struct ColumnIndices(int Qty, int Price, int Amount);

    private sealed class ColumnStrategy
    {
        public ColumnStrategy(bool fromRight, int qty, int price, int amount)
        {
            FromRight = fromRight;
            QtyOffset = qty;
            PriceOffset = price;
            AmountOffset = amount;
        }

        public bool FromRight { get; }
        public int QtyOffset { get; }
        public int PriceOffset { get; }
        public int AmountOffset { get; }
        public int Score { get; set; }
        public double Confidence { get; set; }

        /// <summary>
        /// True when the columns read qty → price → amount in visual order.
        /// Ties are common (qty 1 × price 2.50 and price 1 × qty 2.50 both
        /// validate), so this breaks them toward the conventional layout.
        /// </summary>
        public bool IsMonotonic => FromRight
            ? QtyOffset > PriceOffset && PriceOffset > AmountOffset
            : QtyOffset < PriceOffset && PriceOffset < AmountOffset;

        public ColumnIndices? Resolve(int tokenCount, int minTokens)
        {
            if (tokenCount < minTokens)
            {
                return null;
            }

            if (!FromRight)
            {
                return QtyOffset < tokenCount && PriceOffset < tokenCount && AmountOffset < tokenCount
                    ? new ColumnIndices(QtyOffset, PriceOffset, AmountOffset)
                    : null;
            }

            var qty = tokenCount - 1 - QtyOffset;
            var price = tokenCount - 1 - PriceOffset;
            var amount = tokenCount - 1 - AmountOffset;
            return qty >= 0 && price >= 0 && amount >= 0
                ? new ColumnIndices(qty, price, amount)
                : null;
        }
    }
}
