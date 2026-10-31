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
    private static readonly string[] InvoiceKeywords = { "Invoice", "Order", "Receipt", "INV" };
    private static readonly string[] BuyerNameKeywords = { "Buyer", "Customer", "Client" };

    private static readonly string[] NonItemKeywords =
    {
        "total", "subtotal", "sub total", "tax", "vat", "balance", "change",
        "tendered", "paid", "payment", "discount", "rounding", "items count",
        "order no", "invoice"
    };

    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
        "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "dd MMM yyyy HH:mm", "dd MMM yyyy"
    };

    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex InvoiceNumberPattern = new(
        @"(?i)\b(?:invoice\s*(?:no|number)?|order\s*no\.?|number)\s*[:#\-]?\s*(?<value>[A-Z]{2,5}[\-/]?\d{3,}|\d{4,})",
        RegexOptions.Compiled);

    private const string InvoiceNumberToken = @"\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?";
    private const string InvoiceQuantityToken = @"\d{1,3}(?:,\d{3})*(?:\.\d+)?|\d+(?:\.\d+)?";

    // Invoice PDFs often collapse the table into one line, e.g.
    // "1M113.040.00%15.00%M113.04M130.00".
    private static readonly Regex InvoiceRowPattern = new(
        @"^(?<name>.*?)\s*(?<qty>" + InvoiceQuantityToken + @")\s*(?:[A-Z]{1,3}\s*)?(?<price>" + InvoiceNumberToken + @")\s*" +
        @"(?<disc>" + InvoiceNumberToken + @")\s*%\s*(?<vat>" + InvoiceNumberToken + @")\s*%\s*" +
        @"(?:[A-Z]{1,3}\s*)?(?<exclusive>" + InvoiceNumberToken + @")" +
        @"(?:\s*(?:[A-Z]{1,3}\s*)?(?<inclusive>" + InvoiceNumberToken + @"))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrencyBeforeAmountPattern = new(
        @"(?<![A-Za-z])M\s*\d{1,3}(?:,\d{3})*\.\d{2}(?![0-9])",
        RegexOptions.Compiled);

    private static readonly Regex BuyerLabelPattern = new(
        @"^(VAT|TIN|No\.?|Name|Address|Email|Phone|Telephone)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountLinePattern = new(
        @"^[A-Z]{0,3}\s*-?\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Two-line item layout used by many fiscal registers:
    //   "34400 x 2.400"  (unit price x quantity)
    //   "BIRTHDAY-DICORAN *82,560.00"  (name and line subtotal)
    private static readonly Regex TwoLineQuantityPattern = new(
        @"^(?<price>[\d,]+(?:\.\d+)?)\s*[xX]\s*(?<qty>[\d,]+(?:\.\d+)?)$",
        RegexOptions.Compiled);

    private static readonly Regex TwoLineNameAmountPattern = new(
        @"^(?<name>.+?)\s*\*?\s*(?<amount>-?[\d,]+\.\d{2})$",
        RegexOptions.Compiled);

    private readonly ParseConfig _config;

    public ReceiptParser(ParseConfig config)
    {
        _config = config;
    }

    public ParseResult Parse(string text)
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
        if (strategy is not null && strategy.Confidence < 0.6)
        {
            warnings.Add($"Low column confidence ({strategy.Confidence:P0}); verify the parsed items.");
        }

        var items = new List<ParsedItem>();
        decimal total = 0;
        decimal taxTotal = 0;

        if (strategy is not null)
        {
            for (var i = start; i < end && i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || IsNonItemLine(line))
                {
                    continue;
                }

                var tokens = SplitTokens(line);
                if (tokens.Length < 4)
                {
                    continue;
                }

                var indices = strategy.Resolve(tokens.Length);
                if (indices is null)
                {
                    continue;
                }

                if (!TryParseMoney(tokens[indices.Value.Qty], out var quantity) ||
                    !TryParseMoney(tokens[indices.Value.Price], out var unitPrice) ||
                    !TryParseMoney(tokens[indices.Value.Amount], out var amount))
                {
                    continue;
                }

                if (quantity <= 0 || amount < 0)
                {
                    continue;
                }

                var name = BuildItemName(tokens, indices.Value);
                if (name.Length == 0)
                {
                    name = "Item";
                }

                items.Add(CreateItem(name, quantity, unitPrice, amount, out var lineTax));
                total += amount;
                taxTotal += lineTax;
            }
        }

        // Fiscal registers often wrap an item over two lines: "price x qty"
        // followed by "name *subtotal". Only used when the single-line
        // column layout produced nothing, so the formats never mix.
        if (items.Count == 0)
        {
            ParseTwoLineItems(lines, start, end, items, ref total, ref taxTotal, warnings);
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
        if (printedTotal is not null && Math.Abs(printedTotal.Value - total) > 0.05m)
        {
            return ParseResult.Review(
                $"Parsed item total {total:0.00} does not match the printed total {printedTotal.Value:0.00}. " +
                "Discounts or non-item charges are not supported; refusing to fiscalise.",
                warnings);
        }

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

        var buyer = new ParsedBuyer
        {
            Name = ExtractBuyerName(lines),
            VatNumber = ExtractBuyerVat(lines),
            Tin = ExtractBuyerTin(lines),
            Email = ExtractEmail(lines),
            Phone = ExtractValue(lines, new[] { _config.CustomerTelephoneNumber, "Phone", "Tel" }),
            Address = ExtractValue(lines, new[] { _config.CustomerAddress, "Address" })
        };

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

    private ParseResult? TryParseInvoiceTable(string[] lines, string text)
    {
        var header = FindInvoiceTableHeader(lines);
        if (header < 0)
        {
            return null;
        }

        var items = new List<ParsedItem>();
        var warnings = new List<string>();
        var pendingName = new StringBuilder();
        decimal total = 0;
        decimal taxTotal = 0;

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

            var match = InvoiceRowPattern.Match(line);
            if (!match.Success)
            {
                if (pendingName.Length > 0)
                {
                    pendingName.Append(' ');
                }

                pendingName.Append(line);
                continue;
            }

            if (!TryParseInvoiceRow(match, pendingName.ToString(), out var item, out var lineTax, out var rowError))
            {
                warnings.Add(rowError);
                continue;
            }

            items.Add(item);
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
        if (printedTotal is not null && Math.Abs(printedTotal.Value - total) > 0.05m)
        {
            return ParseResult.Review(
                $"Parsed invoice item total {total:0.00} does not match the printed total {printedTotal.Value:0.00}. " +
                "The invoice table was not fiscalised.",
                warnings);
        }

        return BuildParsedResult(lines, text, items, total, taxTotal, warnings);
    }

    private static int FindInvoiceTableHeader(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var normalized = CollapseInvoiceHeader(lines[i]);
            if (normalized.Contains("DESCRIPTION", StringComparison.Ordinal) &&
                normalized.Contains("QUANTITY", StringComparison.Ordinal) &&
                (normalized.Contains("EXCLUSIVETOTAL", StringComparison.Ordinal) ||
                 normalized.Contains("INCLUSIVETOTAL", StringComparison.Ordinal) ||
                 normalized.Contains("EXCLTOTAL", StringComparison.Ordinal) ||
                 normalized.Contains("INCLTOTAL", StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string CollapseInvoiceHeader(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool IsInvoiceTableEnd(string line)
    {
        var lower = line.Trim().ToLowerInvariant();
        return lower.StartsWith("banking details", StringComparison.Ordinal) ||
               lower.StartsWith("total", StringComparison.Ordinal) ||
               lower.StartsWith("sub total", StringComparison.Ordinal) ||
               lower.StartsWith("grand total", StringComparison.Ordinal) ||
               lower.StartsWith("balance due", StringComparison.Ordinal) ||
               lower.StartsWith("payment", StringComparison.Ordinal) ||
               lower.StartsWith("terms", StringComparison.Ordinal) ||
               lower.StartsWith("thank you", StringComparison.Ordinal);
    }

    private static bool TryParseInvoiceRow(
        Match match,
        string pendingName,
        out ParsedItem item,
        out decimal lineTax,
        out string error)
    {
        item = null!;
        lineTax = 0;
        error = "";

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
        if (Math.Abs(expectedExclusive - exclusiveTotal) > 0.05m)
        {
            error = $"Invoice row qty x price ({expectedExclusive:0.00}) does not match its exclusive total ({exclusiveTotal:0.00}).";
            return false;
        }

        var amount = hasInclusive ? inclusiveTotal : Math.Round(exclusiveTotal * (1m + vatRate), 2);
        if (hasInclusive)
        {
            var expectedInclusive = Math.Round(exclusiveTotal * (1m + vatRate), 2);
            if (Math.Abs(expectedInclusive - amount) > 0.05m)
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

        var taxType = vatRate <= 0m ? "ZERO_RATED" : "STANDARD";
        item = new ParsedItem(name, quantity, amount / quantity, Math.Round(amount, 2), taxType, vatRate);
        return true;
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
            Buyer = new ParsedBuyer
            {
                Name = ExtractBuyerName(lines),
                VatNumber = ExtractBuyerVat(lines),
                Tin = ExtractBuyerTin(lines),
                Email = ExtractEmail(lines),
                Phone = ExtractValue(lines, new[] { _config.CustomerTelephoneNumber, "Phone", "Tel" }),
                Address = ExtractValue(lines, new[] { _config.CustomerAddress, "Address" })
            },
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

                    var tokens = SplitTokens(line);
                    var indices = strategy.Resolve(tokens.Length);
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
                    var error = Math.Abs(expected - amount);

                    if (error <= 0.05m || (amount != 0 && error / Math.Max(amount, expected) <= 0.02m))
                    {
                        valid++;
                        totalError += error;
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

    private ParsedItem CreateItem(string name, decimal quantity, decimal unitPrice, decimal amount, out decimal lineTax)
    {
        var taxType = "STANDARD";
        var taxRate = (decimal)_config.VatRate;

        if (!string.IsNullOrWhiteSpace(_config.NonTaxSymbol) &&
            name.Contains(_config.NonTaxSymbol, StringComparison.OrdinalIgnoreCase))
        {
            taxType = "EXEMPT";
            taxRate = 0m;
        }
        else if (taxRate <= 0m)
        {
            taxType = "ZERO_RATED";
            taxRate = 0m;
        }

        lineTax = CalculateLineTax(amount, taxRate);
        return new ParsedItem(name, quantity, unitPrice, amount, taxType, taxRate);
    }

    private void ParseTwoLineItems(
        string[] lines,
        int start,
        int end,
        List<ParsedItem> items,
        ref decimal total,
        ref decimal taxTotal,
        List<string> warnings)
    {
        var candidates = 0;

        for (var i = start; i < end - 1 && i < lines.Length - 1; i++)
        {
            var quantityMatch = TwoLineQuantityPattern.Match(lines[i].Trim());
            if (!quantityMatch.Success)
            {
                continue;
            }

            var nameLine = lines[i + 1].Trim();
            if (nameLine.Length == 0 || IsNonItemLine(nameLine))
            {
                continue;
            }

            var nameMatch = TwoLineNameAmountPattern.Match(nameLine);
            if (!nameMatch.Success)
            {
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
            var error = Math.Abs(expected - amount);
            if (error > 0.05m && !(amount != 0 && error / Math.Max(amount, expected) <= 0.02m))
            {
                continue;
            }

            items.Add(CreateItem(name, quantity, unitPrice, amount, out var lineTax));
            total += amount;
            taxTotal += lineTax;
            i++; // the name line belongs to this item
        }

        if (candidates > 0 && items.Count == 0)
        {
            warnings.Add("Two-line items were found but qty × price did not match the printed amounts.");
        }
    }

    private static string BuildItemName(string[] tokens, ColumnIndices indices)
    {
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

    private static bool IsNonItemLine(string line)
    {
        var lower = line.ToLowerInvariant();
        foreach (var keyword in NonItemKeywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return lower.Contains("---") || lower.Contains("===");
    }

    private string? ExtractInvoiceNumber(string[] lines)
    {
        var value = ExtractValue(lines, new[] { _config.InvoiceNumber });
        if (string.IsNullOrEmpty(value))
        {
            foreach (var line in lines)
            {
                var match = InvoiceNumberPattern.Match(line);
                if (match.Success)
                {
                    value = match.Groups["value"].Value;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(value))
        {
            value = ExtractValue(lines, InvoiceKeywords);
        }

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        value = Regex.Replace(value, @"[^A-Za-z0-9\-/]", "").Trim('-', '/');
        return value.Length is 0 or > 40 ? null : value;
    }

    private string? ExtractBuyerName(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.Equals(line, "Cash Customer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(line, "Walk-in Customer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(line, "Walk in Customer", StringComparison.OrdinalIgnoreCase))
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

        name = ExtractValue(lines, BuyerNameKeywords);
        name = CleanBuyerName(name);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? CleanBuyerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = Regex.Replace(value.Trim(), @"^Name\s*[:=\-]?\s*", "", RegexOptions.IgnoreCase);
        if (name.Length == 0 || BuyerLabelPattern.IsMatch(name) || !name.Any(char.IsLetter))
        {
            return null;
        }

        return name.Length > 100 ? name[..100] : name;
    }

    private string? ExtractBuyerVat(string[] lines)
    {
        var value = ExtractValue(lines, new[]
        {
            _config.CustomerVATNumber,
            "Customer VAT",
            "Buyer VAT",
            "Client VAT"
        });
        return CleanTaxId(value);
    }

    private string? ExtractBuyerTin(string[] lines)
    {
        var value = ExtractValue(lines, new[]
        {
            _config.CustomerTIN,
            "Customer TIN",
            "Buyer TIN",
            "Client TIN"
        });
        return CleanTaxId(value);
    }

    private static string? CleanTaxId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = Regex.Match(value, @"[A-Za-z0-9\-]{4,}").Value;
        return cleaned.Length == 0 || cleaned.Length > 40 ? null : cleaned;
    }

    private static string? ExtractEmail(string[] lines)
    {
        foreach (var line in lines)
        {
            var match = EmailPattern.Match(line);
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
        var value = ExtractValue(lines, new[] { "Date", "Dated" }) ?? ExtractValueOnNextLine(lines, new[] { "Date", "Dated" });
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
            !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return null;
        }

        if (parsed.Year < 2000 || parsed.Year > 2100)
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

        if (lines.Any(line => CurrencyBeforeAmountPattern.IsMatch(line)))
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

    private static decimal? ExtractPrintedTotal(string[] lines)
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

    private static decimal? ExtractAmountFromLineOrNext(string[] lines, int index)
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
            if (AmountLinePattern.IsMatch(next) && TryParseMoney(next, out var value))
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

        public ColumnIndices? Resolve(int tokenCount)
        {
            if (tokenCount < 4)
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
