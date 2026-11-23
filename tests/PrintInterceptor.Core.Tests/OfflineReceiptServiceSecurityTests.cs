using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.State;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class OfflineReceiptServiceSecurityTests
{
    [Fact]
    public void OfflineSigningWithoutASecretStoreFailsClosedAndScrubsLegacyKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var stateDirectory = Path.Combine(directory, "state");
        var cachePath = Path.Combine(stateDirectory, "offline-provisioning.json");
        const string legacyKey = "legacy-plaintext-private-key";

        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(cachePath,
                "{\"privateKeyPem\":\"" + legacyKey + "\"," +
                "\"state\":{\"deviceId\":\"123\",\"zimraPrivateKey\":\"" + legacyKey + "\"}," +
                "\"fetchedAt\":\"2026-01-01T00:00:00Z\"}");

            var config = new InterceptorConfig
            {
                Storage = new StorageConfig { DataDirectory = directory },
                Offline = new OfflineConfig { Enabled = true }
            };
            var chain = new FiscalChainStore(stateDirectory, NullLog.Instance);

            using var service = new OfflineReceiptService(config, chain, NullLog.Instance);

            Assert.False(service.Ready);
            Assert.NotNull(service.ConfigurationError);
            Assert.Contains("DPAPI", service.ConfigurationError!);
            Assert.Contains("Refresh from FiscalStack", service.ConfigurationError!);
            Assert.DoesNotContain(legacyKey, File.ReadAllText(cachePath));

            Assert.False(service.TryIssue(new ParsedReceipt { Items = [] }, out _, out var error));
            Assert.Contains("DPAPI", error);
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
    public void DisabledOfflineSigningDoesNotRequireASecretStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));

        try
        {
            var config = new InterceptorConfig { Storage = new StorageConfig { DataDirectory = directory } };
            var chain = new FiscalChainStore(Path.Combine(directory, "state"), NullLog.Instance);

            using var service = new OfflineReceiptService(config, chain, NullLog.Instance);

            Assert.False(service.Enabled);
            Assert.Null(service.ConfigurationError);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
