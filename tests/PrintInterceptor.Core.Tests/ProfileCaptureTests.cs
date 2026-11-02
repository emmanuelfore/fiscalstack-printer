using System.Text.Json;
using System.Text.Json.Serialization;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

/// <summary>
/// The capture is the only path that lets a till fiscalise a POS nobody has written rules for, so it
/// is held to two things: it must actually make the unknown receipt parse, and it must never change
/// what a document <em>is</em> or write a rule the parser cannot read. Everything here runs the
/// capture's own preview — the same text parsed with the suggested rules — because that is what the
/// operator sees before saving.
/// </summary>
public class ProfileCaptureTests : IDisposable
{
    private static readonly JsonSerializerOptions ProfileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly string _builtInDirectory;
    private readonly string _userDirectory;

    public ProfileCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "profile-capture-tests", Guid.NewGuid().ToString("N"));
        _builtInDirectory = Path.Combine(_root, "app", "profiles");
        _userDirectory = Path.Combine(_root, "programdata", "profiles");
        Directory.CreateDirectory(_builtInDirectory);
        Directory.CreateDirectory(_userDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ParseProfileStore Store() => new(_builtInDirectory, _userDirectory);

    /// <summary>A till the built-in rules have never seen: column words, labels and date word of its own.</summary>
    private const string UnknownPos = """
        KOPKO FRESH MARKET
        CASH SALE
        SLIP NO: 4471
        Printed: 05/11/2026 14:02
        SOLD TO: Mrs Chipo Moyo
        Physical Address: 12 Koparo Avenue, Harare
        ZIMRA TIN: 20394837
        Customer VAT NO: 9876543
        Cell: 0772 555 111
        Item        Qty     Price     Disc %    VAT %    Total
        Bread 250g    2      1.50      0 %      15 %      3.00
        Milk 500ml    1      4.25      0 %      15 %      4.25
        Zero Sugar Coke 3    2.00      0 %       0 %      6.00
        TOTAL 14.34
        CASH 20.00
        CHANGE 5.66
        THANK YOU FOR SHOPPING
        """;

    /// <summary>The layout the built-in rules already describe.</summary>
    private const string KnownPos = """
        Store Demo
        USD Cash Sale
        Order No.: INV-1001
        Buyer Name: Acme Ltd
        Buyer TIN: 1234567890
        Buyer VAT: 123456789
        Date: 29/10/2026 10:15
        --------------------------------
        Bread Loaf 1.00 2.50 2.50
        Fresh Milk 2.00 1.25 2.50
        --------------------------------
        Items count: 2
        Total: 5.00
        Payment: CASH
        """;

    private static CaptureSuggestion AssertSuggestion(ProfileCapture capture, string property) =>
        Assert.Single(capture.Suggestions.Where(s => s.Property == property));

    [Fact]
    public void CapturesTheLayoutOfAPosTheTillHasNeverSeen()
    {
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());

        Assert.False(capture.Current.IsSuccess);
        Assert.Equal(ParseStatus.NeedsReview, capture.Current.Result.Status);
        Assert.True(capture.Improved);
        Assert.True(capture.HasSuggestions);

        Assert.Equal(ParseStatus.Parsed, capture.Suggested.Result.Status);
        Assert.Equal(3, capture.Suggested.Items);
        Assert.Equal(14.34m, capture.Suggested.Receipt!.Total);
    }

    [Fact]
    public void NamesTheColumnsThatMakeTheItemTableReadable()
    {
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());

        var required = AssertSuggestion(capture, "itemTableHeaderRequires");
        Assert.Equal(new[] { "ITEM", "QTY" }, required.Values);
        Assert.Equal(10, required.Line);

        var totals = AssertSuggestion(capture, "itemTableHeaderAnyOf");
        Assert.Contains("TOTAL", totals.Values);
        Assert.StartsWith("EXCLUSIVETOTAL", totals.Values[0]);
    }

    [Fact]
    public void CapturesTheLabelsThisPosPrintsItsBuyerFieldsUnder()
    {
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());

        Assert.Equal(new[] { "SOLD TO" }, AssertSuggestion(capture, "customerName").Values);
        Assert.Equal(new[] { "ZIMRA TIN" }, AssertSuggestion(capture, "customerTIN").Values);
        Assert.Equal(new[] { "SLIP NO" }, AssertSuggestion(capture, "invoiceNumber").Values);
        Assert.Contains("Printed", AssertSuggestion(capture, "dateLabels").Values);

        // The preview is the proof: these are the values a ZIMRA invoice above the threshold files.
        var buyer = capture.Suggested.Receipt!.Buyer;
        Assert.Equal("Mrs Chipo Moyo", buyer.Name);
        Assert.Equal("20394837", buyer.Tin);
        Assert.Equal("9876543", buyer.VatNumber);
        Assert.Equal("4471", capture.Suggested.Receipt.InvoiceNumber);
    }

    [Fact]
    public void DoesNotOfferABlockMarkerTheParserWouldReadFromTheWrongLine()
    {
        // "Total" names this till's last column as well as its footer, and the item block is bounded
        // by the first line that contains the marker — so "total" would end the block at the header.
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());

        Assert.DoesNotContain(capture.Suggestions, suggestion => suggestion.Property == "productEndLine");
        Assert.DoesNotContain(capture.Suggestions, suggestion => suggestion.Property == "productStartLine");
        Assert.DoesNotContain(capture.Suggestions, suggestion => suggestion.Property == "itemTableEndMarkers");
    }

    [Fact]
    public void StatesOnlyWhatTheBuiltInRulesDoNotAlreadySay()
    {
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());

        var json = JsonSerializer.Serialize(capture.Rules, ProfileJson);
        foreach (var forbidden in new[]
                 {
                     "vatRate", "vatFlag", "currency", "currencies", "paymentMethod", "taxSymbol", "nonTaxSymbol"
                 })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", json);
        }

        Assert.Contains("\"invoiceNumber\":\"SLIP NO\"", json);
        Assert.DoesNotContain("customerVatNumber", json);
    }

    [Fact]
    public void LeavesAReceiptTheBuiltInRulesAlreadyReadAlone()
    {
        var capture = ProfileCapturer.Capture(KnownPos, new ParseConfig());

        Assert.True(capture.Current.IsSuccess);
        Assert.False(capture.HasSuggestions);
        Assert.False(capture.Improved);
        Assert.Equal("{}", JsonSerializer.Serialize(capture.Rules, ProfileJson));
        Assert.Empty(capture.Notes);
    }

    [Fact]
    public void AppliesTheCaptureOnAFreshTillThroughTheProfileStore()
    {
        var capture = ProfileCapturer.Capture(UnknownPos, new ParseConfig());
        var store = Store();

        var saved = store.Save(new ParseProfile
        {
            Name = "kopko-till",
            Title = "Kopko Fresh Market till",
            Description = "Captured from a live job.",
            Rules = capture.Rules
        });

        var reloaded = Store().Load(saved, builtIn: false);
        var config = new ParseConfig();
        reloaded.Rules.ApplyTo(config);

        Assert.Equal("SLIP NO", config.InvoiceNumber);
        var result = new ReceiptParser(config).Parse(UnknownPos);
        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal(capture.Suggested.Items, result.Receipt!.Items.Count);
        Assert.Equal(capture.Suggested.Receipt!.Total, result.Receipt.Total);
    }

    [Fact]
    public void CapturesTheLabelThatCarriesTheInvoiceACreditNoteReverses()
    {
        const string text = """
            KOPKO FRESH MARKET
            CREDIT NOTE
            SLIP NO: 4472
            Reversal Ref: 4471
            Printed: 05/11/2026 15:10
            SOLD TO: Mrs Chipo Moyo
            Item        Qty     Price     Disc %    VAT %    Total
            Milk 500ml    1      4.25      0 %      15 %      4.25
            TOTAL 4.89
            """;

        var capture = ProfileCapturer.Capture(text, new ParseConfig());
        var labels = AssertSuggestion(capture, "corrections.originalInvoiceLabels").Values;

        Assert.Contains("Reversal Ref", labels);
        Assert.Contains("Original Invoice", labels);

        var receipt = capture.Suggested.Receipt;
        Assert.NotNull(receipt);
        Assert.Equal("CreditNote", receipt!.TransactionType);
        Assert.Equal("4472", receipt.InvoiceNumber);
        Assert.Equal("4471", receipt.RelatedInvoiceNumber);
    }

    [Fact]
    public void NeverGuessesWhatADocumentIs()
    {
        const string text = """
            KOPKO FRESH MARKET
            STORNO
            SLIP NO: 4473
            Item        Qty     Price     Disc %    VAT %    Total
            Milk 500ml    1      4.25      0 %      15 %      4.25
            TOTAL 4.89
            """;

        var capture = ProfileCapturer.Capture(text, new ParseConfig());

        Assert.DoesNotContain(capture.Suggestions, s => s.Property.StartsWith("corrections", StringComparison.Ordinal)
                                                       && !s.Property.Contains("originalInvoice", StringComparison.Ordinal));
        var note = Assert.Single(capture.Notes, n => n.Contains("STORNO", StringComparison.Ordinal));
        Assert.Contains("parse.corrections", note);
        Assert.Contains("credit notes", note);
    }

    [Fact]
    public void ReportsATaxCodeTheCurrentRulesDoNotNameWithoutBucketingIt()
    {
        const string text = KnownPos + "\n";
        var withCode = text.Replace("Bread Loaf 1.00 2.50 2.50", "Bread Loaf 1.00 2.50 2.50 G");

        var capture = ProfileCapturer.Capture(withCode, new ParseConfig());
        var note = Assert.Single(capture.Notes, n => n.Contains("parse.taxRules", StringComparison.Ordinal));

        Assert.Contains("G", note);
        Assert.DoesNotContain(capture.Suggestions, s => s.Property == "taxRules");
    }

    [Fact]
    public void SaysWhenThePrintedTotalIsAnnouncedByWordsTheParserCannotLearn()
    {
        var noTotalWord = KnownPos.Replace("Total: 5.00", "AMOUNT DUE: 5.00");

        var capture = ProfileCapturer.Capture(noTotalWord, new ParseConfig());
        var note = Assert.Single(capture.Notes, n => n.Contains("fixed in the parser", StringComparison.Ordinal));

        Assert.Contains("AMOUNT DUE: 5.00", note);
        Assert.DoesNotContain(capture.Suggestions, s => s.Property.Contains("total", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaysWhichAdditionsThePreviewStillCannotRead()
    {
        // A date the POS printed wrong (31 February): both orders are a fair guess from the paper and
        // neither can ever read, which is the case the preview has to admit rather than hide.
        var impossibleDate = KnownPos.Replace("Date: 29/10/2026 10:15", "Date: 31/02/2026 10:15");

        var capture = ProfileCapturer.Capture(impossibleDate, new ParseConfig());
        var suggestion = AssertSuggestion(capture, "dateFormats");
        Assert.Contains(suggestion.Values, format => format.Contains("MM/dd", StringComparison.Ordinal));

        Assert.True(capture.Current.IsSuccess);
        Assert.DoesNotContain(capture.Suggested.Fields, field => field.Name == "Document date" && field.Found);
        Assert.Contains(capture.Notes, n => n.Contains("Document date", StringComparison.Ordinal));
    }

    [Fact]
    public void HandlesAnEmptyOrUnreadableJob()
    {
        foreach (var text in new[] { "", "   \n  ", "\n", "gs: unknown operator" })
        {
            var capture = ProfileCapturer.Capture(text, new ParseConfig());
            Assert.NotNull(capture.Current.Result);
            Assert.NotNull(capture.Suggested.Result);
            Assert.Equal("{}", JsonSerializer.Serialize(capture.Rules, ProfileJson));
        }
    }

    [Fact]
    public void KeepsTheArithmeticGuardsOutsideTheCapture()
    {
        // Items 13.25 against a printed 9.00: the disagreement is the guard's, not a layout rule.
        var disagreeing = UnknownPos.Replace("TOTAL 14.34", "TOTAL 9.00");
        var capture = ProfileCapturer.Capture(disagreeing, new ParseConfig());

        Assert.DoesNotContain(capture.Suggestions, s => s.Property.StartsWith("limits", StringComparison.Ordinal));
        Assert.Equal(ParseStatus.NeedsReview, capture.Suggested.Result.Status);
        Assert.Contains(capture.Notes, n => n.Contains("no profile can fix", StringComparison.Ordinal));
    }
}
