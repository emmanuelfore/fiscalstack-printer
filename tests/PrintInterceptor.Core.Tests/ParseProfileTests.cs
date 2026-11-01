using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class ParseProfileTests : IDisposable
{
    private readonly string _builtInDirectory;
    private readonly string _userDirectory;

    public ParseProfileTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "parse-profile-tests", Guid.NewGuid().ToString("N"));
        _builtInDirectory = Path.Combine(root, "app", "profiles");
        _userDirectory = Path.Combine(root, "programdata", "profiles");
        Directory.CreateDirectory(_builtInDirectory);
        Directory.CreateDirectory(_userDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_builtInDirectory)!, true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    private ParseProfileStore Store() => new(_builtInDirectory, _userDirectory);

    /// <summary>The store as a till sees it, with the profiles that ship in the application folder.</summary>
    private ParseProfileStore ShippedStore() => new(Path.Combine(AppContext.BaseDirectory, "profiles"), _userDirectory);

    private static ParseProfile Profile(string name) => new()
    {
        Name = name,
        Title = name + " layout",
        Rules = new ParseProfileRules { InvoiceNumber = name + " No." }
    };

    private string Write(ParseProfile profile, string directory)
    {
        var path = Path.Combine(directory, profile.Name + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        return path;
    }

    [Fact]
    public void ShippedProfilesLoadAndDescribeThemselves()
    {
        var store = ShippedStore();

        var profiles = store.List();

        Assert.Empty(store.LoadErrors);
        Assert.Contains("sage", profiles.Select(p => p.Name));
        Assert.Contains("zohobooks", profiles.Select(p => p.Name));
        Assert.All(profiles, profile => Assert.True(profile.BuiltIn));
    }

    [Fact]
    public void TheSageProfileIsExactlyTheBuiltInRuleSet()
    {
        // sage.json is the reference template: applying it must be a no-op on defaults.
        var profile = ShippedStore().Find("sage")!;
        var config = new ParseConfig();
        var before = config.ExportRules();

        profile.Rules.ApplyTo(config);

        Assert.Equal(Json(before), Json(config.ExportRules()));
    }

    [Fact]
    public void TheZohobooksProfileChangesOnlyTheLinesItStates()
    {
        var profile = ShippedStore().Find("zohobooks")!;
        var config = new ParseConfig();

        profile.Rules.ApplyTo(config);

        Assert.Equal("Number", config.InvoiceNumber);
        Assert.Contains("Invoice Date", config.DateLabels);
        Assert.Contains("notes", config.ItemTableEndMarkers);

        // Untouched by the profile: still the sage defaults.
        Assert.Equal("Order No.", config.ProductStartLine);
        Assert.Equal("Items count", config.ProductEndLine);
        Assert.Equal(new ParseConfig().Patterns.InvoiceRow, config.Patterns.InvoiceRow);
        Assert.Equal(new ParseConfig().Limits.LineTolerance, config.Limits.LineTolerance);
    }

    [Fact]
    public void AProfileDrivesWhatTheParserFinds()
    {
        const string text = """
            Store Demo
            Zoho No.: 9915-AB
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Total: 2.50
            """;

        var unprofiled = new ReceiptParser(new ParseConfig()).Parse(text);
        Assert.Null(unprofiled.Receipt!.InvoiceNumber);

        var config = new ParseConfig();
        new ParseProfile
        {
            Name = "zoho",
            Title = "Zoho",
            Rules = new ParseProfileRules { InvoiceNumber = "Zoho No." }
        }.Rules.ApplyTo(config);

        var parsed = new ReceiptParser(config).Parse(text);

        Assert.Equal(ParseStatus.Parsed, parsed.Status);
        Assert.Equal("9915-AB", parsed.Receipt!.InvoiceNumber);
    }

    [Fact]
    public void SavedProfilesAreListedFromTheUserFolder()
    {
        var store = Store();
        var path = store.Save(Profile("mypos"));

        var profiles = store.List();

        Assert.Equal(Path.Combine(_userDirectory, "mypos.json"), path);
        Assert.Equal(new[] { "mypos" }, profiles.Select(p => p.Name));
        Assert.False(profiles.Single().BuiltIn);
    }

    [Fact]
    public void AUserProfileReplacesTheShippedOneOfTheSameName()
    {
        File.Copy(ShippedProfile("sage"), Path.Combine(_builtInDirectory, "sage.json"), true);
        var store = Store();
        var replacement = Profile("sage");
        replacement.Title = "Our Sage, tuned";
        var installed = store.Save(replacement);

        var sage = store.List().Single(p => p.Name == "sage");

        Assert.False(sage.BuiltIn);
        Assert.Equal(installed, sage.FilePath);
        Assert.Equal("Our Sage, tuned", sage.Title);
    }

    [Fact]
    public void ImportCopiesAReceivedFileAndRefusesToOverwriteSilently()
    {
        var received = Path.Combine(Path.GetTempPath(), $"received-{Guid.NewGuid():N}.json");
        Write(Profile("kiosk"), Path.GetTempPath());
        File.Move(Path.Combine(Path.GetTempPath(), "kiosk.json"), received);

        var store = Store();
        var destination = store.Import(received);

        Assert.Equal(Path.Combine(_userDirectory, "kiosk.json"), destination);
        Assert.Contains("kiosk", store.List().Select(p => p.Name));

        var updated = Profile("kiosk");
        updated.Title = "Kiosk v2";
        var secondCopy = Write(updated, Path.GetTempPath());

        var error = Assert.Throws<InvalidDataException>(() => store.Import(secondCopy));
        Assert.Contains("already installed", error.Message);

        store.Import(secondCopy, overwrite: true);
        Assert.Equal("Kiosk v2", store.Find("kiosk")!.Title);
    }

    [Fact]
    public void ABrokenProfileIsReportedInsteadOfVanishing()
    {
        File.WriteAllText(Path.Combine(_userDirectory, "oops.json"), "{ this is not json");
        var store = Store();
        store.Save(Profile("good"));

        var profiles = store.List();

        Assert.Equal(new[] { "good" }, profiles.Select(p => p.Name));
        Assert.Contains(store.LoadErrors, error => error.StartsWith("oops.json"));
    }

    [Fact]
    public void AProfileWithoutGroupsTheParserReadsIsRejected()
    {
        var profile = Profile("broken");
        profile.Rules.Patterns = new ParseProfilePatterns { InvoiceRow = @"^(?<name>.*)$" };

        var errors = Store().Validate(profile);

        Assert.Contains(errors, error => error.Contains("invoiceRow") && error.Contains("qty"));
    }

    [Fact]
    public void AProfileNameThatCouldEscapeTheProfilesFolderIsRejected()
    {
        var errors = Store().Validate(new ParseProfile
        {
            Name = "../escape",
            Title = "Escape",
            Rules = new ParseProfileRules { InvoiceNumber = "No." }
        });

        Assert.Contains(errors, error => error.Contains("letters, digits or dashes"));
    }

    [Fact]
    public void AProfileNeedsToBeRecognisableLater()
    {
        var anonymous = new ParseProfile
        {
            Name = "mystery",
            Rules = new ParseProfileRules { InvoiceNumber = "No." }
        };

        var errors = Store().Validate(anonymous);

        Assert.Contains(errors, error => error.Contains("title or a description"));
    }

    [Fact]
    public void ApplyWritesTheRulesAndRemembersTheProvenance()
    {
        var store = Store();
        store.Save(Profile("mypos"));
        var config = new InterceptorConfig();

        store.Apply("mypos", config);

        Assert.Equal("mypos", config.Parse.Profile);
        Assert.Equal("mypos No.", config.Parse.InvoiceNumber);
    }

    [Fact]
    public void ApplyNamesTheFolderWhenAProfileIsMissing()
    {
        var error = Assert.Throws<InvalidDataException>(() => Store().Apply("ghost", new InterceptorConfig()));

        Assert.Contains("ghost", error.Message);
        Assert.Contains(_userDirectory, error.Message);
    }

    [Fact]
    public void CapturingTheCurrentRulesProducesAValidProfile()
    {
        var config = new InterceptorConfig();
        config.Parse.InvoiceNumber = "Bill No.";
        config.Parse.NonItemKeywords.Add("deposit");

        var profile = ParseProfileStore.Capture("till-one", "Till one", config.Parse);
        var errors = Store().Validate(profile);

        Assert.Empty(errors);
        Assert.Equal("Bill No.", profile.Rules.InvoiceNumber);
        Assert.Equal(config.Parse.Patterns.InvoiceRow, profile.Rules.Patterns!.InvoiceRow);
        Assert.Equal(config.Parse.Limits.RegexTimeoutMilliseconds, profile.Rules.Limits!.RegexTimeoutMilliseconds);
    }

    [Fact]
    public void RemovingAnInstalledProfileBringsTheShippedOneBack()
    {
        File.Copy(ShippedProfile("sage"), Path.Combine(_builtInDirectory, "sage.json"), true);
        var store = Store();
        var installed = store.Save(Profile("sage"));

        Assert.False(store.Find("sage")!.BuiltIn);

        File.Delete(installed);

        Assert.True(store.Find("sage")!.BuiltIn);
    }

    [Theory]
    [InlineData("sage")]
    [InlineData("zohobooks")]
    public void EveryShippedProfileParsesTheReceiptsItClaims(string name)
    {
        const string thermal = """
            Store Demo
            Items count: 1
            Order No.: INV-1
            --------------------------------
            Bread Loaf 1.00 2.50 2.50
            --------------------------------
            Total: 2.50
            """;

        var config = new InterceptorConfig();
        ShippedStore().Apply(name, config);

        var result = new ReceiptParser(config.Parse).Parse(thermal);

        Assert.Equal(ParseStatus.Parsed, result.Status);
        Assert.Equal(2.50m, result.Receipt!.Total);
    }

    [Fact]
    public void ProfileFieldsCannotReachTheNumbersAReceiptIsFiscalisedWith()
    {
        var rules = typeof(ParseProfileRules).GetProperties().Select(p => p.Name);
        var fiscalFields = new[] { "VatRate", "VatFlag", "Currencies", "Currency", "PaymentMethod", "TaxSymbol", "NonTaxSymbol" };

        foreach (var field in fiscalFields)
        {
            Assert.DoesNotContain(field, rules);
        }
    }

    [Fact]
    public void ProfilePropertiesMirrorTheConfigurationTheyWrite()
    {
        AssertMatches<ParseProfileRules, ParseConfig>();
        AssertMatches<ParseProfilePatterns, ParsePatterns>();
        AssertMatches<ParseProfileLimits, ParseLimits>();
    }

    private static void AssertMatches<TRules, TTarget>()
    {
        var targets = typeof(TTarget).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in typeof(TRules).GetProperties())
        {
            Assert.True(targets.Contains(property.Name),
                $"{typeof(TRules).Name}.{property.Name} has no {typeof(TTarget).Name} field to write.");
        }
    }

    private static string ShippedProfile(string name) =>
        Path.Combine(AppContext.BaseDirectory, "profiles", name + ".json");

    private static string Json(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
