using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class SecretStoreTests
{
    [Fact]
    public void DescribeReportsOnlyACoarseLengthBucket()
    {
        const string secret = "sk_live_abcdefghijklmnopqrstuv";

        var description = SecretStore.Describe(secret);

        Assert.Contains("configured", description);
        Assert.Contains("length", description);
        Assert.DoesNotContain("sk_live", description);
        Assert.DoesNotContain(secret, description);
        Assert.DoesNotContain(secret.Length.ToString(), description);
        Assert.Equal("not configured", SecretStore.Describe(null));
        Assert.Equal("not configured", SecretStore.Describe("   "));
    }

    [Fact]
    public void DescribeBucketsLengthsWithoutCountingCharacters()
    {
        Assert.Contains("short", SecretStore.Describe("abcdefghij"));
        Assert.Contains("typical", SecretStore.Describe("abcdefghijklmnopqrstuvwxyz123456"));
        Assert.Contains("long", SecretStore.Describe(new string('x', 80)));
    }

    [Fact]
    public void TrySetRefusesEmptyValuesWithoutCreatingTheStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var store = new SecretStore(Path.Combine(directory, "secrets"), NullLog.Instance);

        try
        {
            Assert.False(store.TrySet(SecretStore.ApiKeyEntry, "   ", out var error));
            Assert.Contains("empty", error.ToLowerInvariant());
            Assert.False(Directory.Exists(store.DirectoryPath));
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
    public void StoreLivesBesideTheConfigFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "till", "config.json");

        Assert.Equal(Path.Combine(root, "till", "secrets"), SecretStore.DefaultDirectory(configPath));
    }

    [Fact]
    public void RestrictHelpersFailClosedWhenAclCannotBeApplied()
    {
        // A path that does not exist cannot be protected. Both helpers must throw
        // instead of quietly returning so callers never write under an open ACL.
        var missing = Path.Combine(Path.GetTempPath(), "printinterceptor-missing-" + Guid.NewGuid().ToString("N"));

        Assert.ThrowsAny<Exception>(() => SecretStore.RestrictFile(missing));
        Assert.ThrowsAny<Exception>(() => SecretStore.RestrictDirectory(missing));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TrySetRoundTripsWithoutLeavingTempFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; the portable behaviour is covered above.
        }

        var directory = Path.Combine(Path.GetTempPath(), "printinterceptor-tests-" + Guid.NewGuid().ToString("N"));
        var store = new SecretStore(Path.Combine(directory, "secrets"), NullLog.Instance);
        const string key = "sk_live_roundtrip_1234567890";

        try
        {
            Assert.True(store.TrySet(SecretStore.ApiKeyEntry, key, out var error), error);
            Assert.True(store.TryGet(SecretStore.ApiKeyEntry, out var read));
            Assert.Equal(key, read);
            Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp"));

            var security = new FileInfo(store.PathFor(SecretStore.ApiKeyEntry)).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
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
