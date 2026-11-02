using PrintInterceptor.Core.Configuration;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class InterceptorConfigTests
{
    [Fact]
    public void ExampleConfigParsesAndValidates()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        Assert.True(File.Exists(path), $"Example config not found at {path}");

        var config = InterceptorConfig.LoadOrCreate(path);

        Assert.Empty(config.Validate());
        Assert.Equal(9100, config.ListenPort);
        Assert.EndsWith("/", config.ApiEndpoint);
        Assert.Equal("1", config.Parse.VatFlag);
        Assert.NotEmpty(config.Parse.Currencies);
        Assert.True(config.SetupCompleted);
    }

    [Fact]
    public void MissingConfigIsCreatedWithDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");

        try
        {
            var config = InterceptorConfig.LoadOrCreate(path);

            Assert.True(File.Exists(path));
            Assert.Empty(config.Validate());
            Assert.True(config.Printing.Enabled);
            Assert.False(config.SetupCompleted);

            var reloaded = InterceptorConfig.LoadOrCreate(path);
            Assert.Equal(config.ListenPort, reloaded.ListenPort);
            Assert.False(reloaded.SetupCompleted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsMalformedSettings()
    {
        var config = new InterceptorConfig
        {
            ListenAddress = "not-an-ip",
            ListenPort = 0,
            ApiEndpoint = "ftp://example.com",
            Printing = new PrintingConfig { PaperWidthMm = 10, Copies = 9, Format = "poster" },
            Parse = new ParseConfig { VatRate = 2 }
        };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("listenAddress"));
        Assert.Contains(errors, e => e.Contains("listenPort"));
        Assert.Contains(errors, e => e.Contains("apiEndpoint"));
        Assert.Contains(errors, e => e.Contains("paperWidthMm"));
        Assert.Contains(errors, e => e.Contains("copies"));
        Assert.Contains(errors, e => e.Contains("printing.format"));
        Assert.Contains(errors, e => e.Contains("vatRate"));
    }

    [Fact]
    public void OriginalTemplateNeedsTheJobBytesItStamps()
    {
        var config = new InterceptorConfig
        {
            ApiKey = "sk_test_example",
            Printing = new PrintingConfig { Template = "original" },
            Storage = new StorageConfig { RetainJobs = false }
        };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("printing.template") && e.Contains("retainJobs"));
    }

    [Fact]
    public void OriginalTemplateWithRetentionIsAccepted()
    {
        var config = new InterceptorConfig
        {
            ApiKey = "sk_test_example",
            Printing = new PrintingConfig { Template = "original" }
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void UnknownTemplateIsRejected()
    {
        var config = new InterceptorConfig
        {
            ApiKey = "sk_test_example",
            Printing = new PrintingConfig { Template = "photo" }
        };

        Assert.Contains(config.Validate(), e => e.Contains("printing.template"));
    }

    [Fact]
    public void TemplateDefaultsToFiscalAndIsNormalised()
    {
        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");

        try
        {
            var created = InterceptorConfig.LoadOrCreate(path);
            Assert.Equal("fiscal", created.Printing.Template);

            File.WriteAllText(path, """
                {
                  "apiKey": "sk_test_example",
                  "printing": { "template": "  ORIGINAL " }
                }
                """);

            var loaded = InterceptorConfig.LoadOrCreate(path);
            Assert.Equal("original", loaded.Printing.Template);
            Assert.True(loaded.Storage.RetainJobs);
            Assert.Empty(loaded.Validate());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void OfflineSigningNeedsOnlyTheApiKey()
    {
        var config = new InterceptorConfig
        {
            ApiKey = "sk_test_example",
            Offline = new OfflineConfig { Enabled = true }
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void OfflineSigningWithoutApiKeyIsRejected()
    {
        var config = new InterceptorConfig
        {
            ApiKey = "",
            Offline = new OfflineConfig { Enabled = true }
        };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("offline.enabled") && e.Contains("apiKey"));
    }

    [Fact]
    public void AWordThatMeansBothACreditAndADebitNoteIsRejected()
    {
        var config = new InterceptorConfig();
        config.Parse.Corrections.CreditNoteKeywords = ["ADJUSTMENT"];
        config.Parse.Corrections.DebitNoteKeywords = ["adjustment"];

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("both a credit and a debit note"));
    }

    [Fact]
    public void ACorrectionKeywordMadeOnlyOfFiguresIsRejected()
    {
        var config = new InterceptorConfig();
        config.Parse.Corrections.CreditNoteKeywords = ["-1234"];

        Assert.Contains(config.Validate(), e => e.Contains("creditNoteKeywords") && e.Contains("no letters"));
    }

    [Fact]
    public void ACorrectionNumberPrefixThatCouldMatchASaleIsRejected()
    {
        var config = new InterceptorConfig();
        config.Parse.Corrections.CreditNoteNumberPrefixes = ["INV", "1", "ABCDEFGH"];

        var errors = config.Validate();

        Assert.Equal(2, errors.Count(e => e.Contains("creditNoteNumberPrefixes")));
    }

    [Fact]
    public void ABlankCorrectionLabelWouldMatchEveryLineAndIsRejected()
    {
        var config = new InterceptorConfig();
        config.Parse.Corrections.OriginalInvoiceLabels = [" "];
        config.Parse.Corrections.ReasonLabels = [""];

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("originalInvoiceLabels") && e.Contains("empty label"));
        Assert.Contains(errors, e => e.Contains("reasonLabels") && e.Contains("empty label"));
    }

    [Fact]
    public void TheExampleConfigCarriesTheBuiltInCorrectionVocabulary()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var example = InterceptorConfig.LoadOrCreate(path).Parse.Corrections;
        var builtIn = new ParseConfig().Corrections;

        Assert.Equal(builtIn.CreditNoteKeywords, example.CreditNoteKeywords);
        Assert.Equal(builtIn.DebitNoteKeywords, example.DebitNoteKeywords);
        Assert.Equal(builtIn.CreditNoteNumberPrefixes, example.CreditNoteNumberPrefixes);
        Assert.Equal(builtIn.OriginalInvoiceLabels, example.OriginalInvoiceLabels);
        Assert.Equal(builtIn.ReasonLabels, example.ReasonLabels);
        Assert.Equal(builtIn.NegativeTotalMeansCreditNote, example.NegativeTotalMeansCreditNote);
    }

    [Fact]
    public void AReconcileIntervalOutsideADayIsRejectedButZeroMeansManualOnly()
    {
        var config = new InterceptorConfig { Reconcile = new ReconcileConfig { IntervalMinutes = 1441 } };
        Assert.Contains(config.Validate(), e => e.Contains("reconcile.intervalMinutes"));

        config.Reconcile.IntervalMinutes = 0;
        Assert.DoesNotContain(config.Validate(), e => e.Contains("reconcile.intervalMinutes"));

        config.Reconcile.Enabled = false;
        config.Reconcile.IntervalMinutes = -5;
        Assert.DoesNotContain(config.Validate(), e => e.Contains("reconcile.intervalMinutes"));
    }

    [Fact]
    public void AnUnboundedReconcileRetryLoopIsRejected()
    {
        var config = new InterceptorConfig { Reconcile = new ReconcileConfig { MaxAttempts = 0 } };
        Assert.Contains(config.Validate(), e => e.Contains("reconcile.maxAttempts"));

        config.Reconcile.MaxAttempts = 11;
        Assert.Contains(config.Validate(), e => e.Contains("reconcile.maxAttempts"));
    }

    [Fact]
    public void AReconcileScanThatWouldReadTheWholeLedgerIsRejected()
    {
        var config = new InterceptorConfig { Reconcile = new ReconcileConfig { ScanPages = 51 } };

        Assert.Contains(config.Validate(), e => e.Contains("reconcile.scanPages"));
    }

    [Fact]
    public void ReSendingWithoutRetainingTheRequestBodyIsRejected()
    {
        var config = new InterceptorConfig { Storage = new StorageConfig { RetainJobs = false } };

        Assert.Contains(config.Validate(), e => e.Contains("reconcile.autoResend") && e.Contains("storage.retainJobs"));

        config.Reconcile.AutoResend = false;
        Assert.DoesNotContain(config.Validate(), e => e.Contains("reconcile.autoResend"));
    }

    [Fact]
    public void TheExampleConfigCarriesTheBuiltInReconcileSettings()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var example = InterceptorConfig.LoadOrCreate(path).Reconcile;
        var builtIn = new ReconcileConfig();

        Assert.Equal(builtIn.Enabled, example.Enabled);
        Assert.Equal(builtIn.IntervalMinutes, example.IntervalMinutes);
        Assert.Equal(builtIn.AutoResend, example.AutoResend);
        Assert.Equal(builtIn.MaxAttempts, example.MaxAttempts);
        Assert.Equal(builtIn.ScanPages, example.ScanPages);
    }
}
