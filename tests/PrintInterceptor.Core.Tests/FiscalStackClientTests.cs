using PrintInterceptor.Core.Fiscalisation;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class FiscalStackClientTests
{
    [Fact]
    public void ProvisioningUsesTheApiKeyAlias()
    {
        var uri = FiscalStackClient.OfflineStateUri("https://fiscalstack.co.zw/api/v1/");

        Assert.Equal("https://fiscalstack.co.zw/api/zimra/offline-state", uri.ToString());
    }

    [Fact]
    public void ProvisioningIgnoresTheApiVersionPrefix()
    {
        var uri = FiscalStackClient.OfflineStateUri("http://localhost:5000/api/v1");

        Assert.Equal("http://localhost:5000/api/zimra/offline-state", uri.ToString());
    }
}
