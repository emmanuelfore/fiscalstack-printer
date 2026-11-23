using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
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

    /// <summary>
    /// A timeout, an early request or a rate limit is a "try again" from HTTP's point of view,
    /// but a fiscalise request is a write: the receipt may already exist. Those answers must
    /// reach reconciliation as unknown, never as a refusal the till can treat as final.
    /// </summary>
    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    public async Task RetryableStatusesAreAmbiguousNotRejected(int status)
    {
        await using var server = new StubHttpServer(_ =>
            StubHttpServer.Json(status, """{"message":"try again"}"""));
        using var client = Client(server.Port);

        var outcome = await client.FiscaliseRawAsync(
            """{"invoiceNumber":"INV-HTTP"}""", CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Ambiguous, outcome.Status);
        Assert.Equal(status, outcome.HttpStatus);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    public async Task DefiniteRefusalsRemainRejected(int status)
    {
        await using var server = new StubHttpServer(_ =>
            StubHttpServer.Json(status, """{"message":"bad request"}"""));
        using var client = Client(server.Port);

        var outcome = await client.FiscaliseRawAsync(
            """{"invoiceNumber":"INV-HTTP"}""", CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Rejected, outcome.Status);
        Assert.Equal(status, outcome.HttpStatus);
    }

    [Fact]
    public async Task RawErrorBodiesNeverReachTheOutcomeOrLog()
    {
        const string pii = "Buyer Jane Doe TIN 123456789 VAT 987654321";
        await using var server = new StubHttpServer(_ =>
            new StubResponse(500, "text/plain", Encoding.UTF8.GetBytes(pii)));
        using var client = Client(server.Port);

        var outcome = await client.FiscaliseRawAsync(
            """{"invoiceNumber":"INV-PII"}""", CancellationToken.None);

        Assert.Equal(FiscalizeOutcomeStatus.Ambiguous, outcome.Status);
        Assert.NotNull(outcome.Error);
        Assert.DoesNotContain("Jane Doe", outcome.Error);
        Assert.Contains("withheld", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static FiscalStackClient Client(int port) => new(
        new InterceptorConfig
        {
            ApiEndpoint = $"http://127.0.0.1:{port}/",
            ApiKey = "sk_test_example"
        },
        NullLog.Instance);
}
