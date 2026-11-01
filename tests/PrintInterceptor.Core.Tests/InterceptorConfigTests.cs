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
}
