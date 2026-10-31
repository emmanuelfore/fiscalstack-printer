using System.Security.Cryptography;
using System.Text;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class OfflineSignerTests
{
    // Test-only key. Never use this key for a real device.
    private const string TestKey = """
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCVaKorsgvb38JX
        OHbKu6vjYE4yFedEJ09GuZkNaFbyd99WutwOilK35unzvdWX7QikYXN4wbwz4V8o
        h4q2XTlkuAY08YjMtbZzU6h6owSAzbvTwJ0YQW08A93smTIhrRt+GDnTpE6O0BNS
        a+FQ3ie4Ow5GRdNakCYUgcaKqjz65MTYs5jS8AxLbDiViabhNwHYYV0QQl7LgB9K
        sQQbskqcuSBJ7xkB0ZS5q/jcToS+PfKPWjnkJsDD1qo+PAj44EAr44y/NXsaM1Xi
        jVogNYD1M3xuzsyFJrG7TntbMs6SjvPV/FDnhh7PZPictqTQJpWxyplfW2FmfW8r
        mPrgG0MfAgMBAAECggEAAZJiXJrt772lhnAcRB1ZCFDm3DuLDPciwfttYqEpy1h+
        U3H8+pMoNAL8t/l8dg9Xo+7HmOS/tNp1JVYntP0ACS675eJ+xBUMuVp2GjdOlOsk
        xtAnDyH5RUqbXemNHcGtujfr57iH9r/hpIwLEhkprVKw8koqvclDHttUHs08qYg0
        B4staMIosGT5WFgdHNCUUKzYIbNO7nO8xPOLG7KDvhGR7B7/x2H5J0QpK3uJnjyP
        fvlfpzRvMByBXPNtW40H3YsI8UgYWr8jwFUphm1I8UkIRPD0yIaKwbq27YoL1eYT
        R9wSlq820Oz1/NhBTrBbj1QFmjD+ogSrgi3+LdlPgQKBgQDQugbKMG2G1NOZvEE+
        ThVQJtXno7RYpqtpQ6gdOYdTdNjEMeCvMWT7dcGKZlur5sQKHFRs0YqMrxS8K6qP
        h6N0fVhzy30KzjqjtIoye8BQOJRBQbdUd2pFtX1AFJFtpcBO5xkfCrMf1bbPMoIa
        kzBSNwotdqT7249SjMKzY5ZGTwKBgQC3P2QBAs+BN/+Kghd4rQ5dgy5sgn9fOARh
        zYnphR6tCnIPEXKAzUYuvTcaJ0JsKS/o4Cu7G5j/IqMGtT/x3tYGiHskLD6Dvqpk
        fheMPv/gFo8mVrGLWshs3SJP3bAeZpLMPjTE9jNNv5YTznnDZPOeDJpLLFuZ7Yn/
        2blreuXSMQKBgQDMXdhYD6Kt6aRlWlXnUzy7BctayobCZi5fGsd4FePSaw3yy4Nb
        BdoB/AJMKHyJi6FW5PYkteYaaTK8K2UMs5nvbOs2NX9xHQt9TkAhvdlGsp82ZcnD
        Akkjh9fi7kCWQzxQBSYrpqZL/UzJAoMw0fz2Ar4MVaakkw15nijkzBVPMQKBgD8t
        gMoEcQxJBTZ4waTebVx6NNA+cbGfe4tItOqo9bmNJ2PgjQyxOSg2HkBLFtB5psXw
        O8OHsUkNZQ2UAdbfEAUymtoO0wLTAqYg/vdgZRGaVYJPU2kLRJyb5Q+KDAV/Mc9v
        Bvx9MZT1n8V2Y26laISuLagLRA2e4vwt0Yqn1YKRAoGADlmiFoIvnI13mMAI0dcJ
        AZfFWlacWqc8FfUpfCK2VA4SaYJpxPYw+taT/99Tsc15kOpqRs54aMmYJDndknWX
        1gzkGzVmRslYrEChscZA1aAqB9VHBmlgIfzU7LxW52i8qCuiHGo/kgVit9N8xzcy
        HeK7BhPQbHFbm4mmgogB1mE=
        -----END PRIVATE KEY-----
        """;

    private static RSA Key()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(TestKey);
        return rsa;
    }

    private static OfflineSignRequest Request(
        IReadOnlyList<ParsedItem>? items = null,
        string receiptType = "FiscalInvoice",
        string deviceId = "1234567890") => new()
    {
        DeviceId = deviceId,
        ReceiptType = receiptType,
        Currency = "USD",
        ReceiptDate = "2026-10-30T10:15:00",
        ReceiptCounter = 35,
        ReceiptGlobalNo = 1056,
        FiscalDayNo = 12,
        PreviousHash = "PREVIOUSHASH",
        TaxInclusive = true,
        StandardTaxPercent = 15m,
        Taxes = OfflineConfig.DefaultTaxes(),
        QrBaseUrl = "https://fdms.zimra.co.zw/",
        Items = items ?? new[]
        {
            new ParsedItem("Bread", 2m, 2.50m, 2.50m, "STANDARD", 0.15m),
            new ParsedItem("Milk", 1m, 1.00m, 1.00m, "ZERO_RATED", 0m),
            new ParsedItem("Rice", 1m, 3.00m, 3.00m, "EXEMPT", 0m)
        }
    };

    [Fact]
    public void MatchesTheServerSignatureVector()
    {
        using var key = Key();
        var signed = OfflineSigner.Sign(Request(), key);

        Assert.Equal(
            "1234567890FISCALINVOICEUSD10562026-10-30T10:15:00900C0300B0.000100A15.0065500PREVIOUSHASH",
            signed.StringToSign);
        Assert.Equal("UdUdaXPhED/rtP6Ft41mbuVSegl7RG+A7obejHY+ZVg=", signed.Hash);
        Assert.Equal(
            "Slosc3zcPy+o9zNvu4XvzVzoUVOKoAFzhngD24WwNyFqqljUeTut7smn+RxTchuhBN9R4GcxwezcL3m0DzzWt9wA18qpQeyoyvGYblsP0wjevUzyAdeYxGrMhJUDmXC0BjgCbtN2bJxHxPj/GHAic8mkahA6Y5t5R9Z52Txteolckz/y21jCxInTGB3w7aY/N7d3JRcANost8JKxNlwUMPu5nQiHE17KpKqmzCC8fXsh68jsCxlkIW3ACechWElpAC/vfcCi2PAu9fhVqFHXseFiOwwFi+dg0rdeUrFn0JjvfPoQ26I5XHm9NI6zLqJdRSn4xJ8XwbL08xiGViKCAQ==",
            signed.Signature);
        Assert.Equal("16FC-FA54-8CE4-DCF0", signed.VerificationCode);
        Assert.Equal(9.00m, signed.ReceiptTotal);
        Assert.Equal(0.65m, signed.TaxTotal);
        Assert.Equal(
            "https://fdms.zimra.co.zw/123456789030102026000000105616FCFA548CE4DCF0",
            signed.QrCodeUrl);
    }

    [Fact]
    public void SignatureVerifiesWithThePublicKey()
    {
        using var key = Key();
        var signed = OfflineSigner.Sign(Request(), key);

        using var publicOnly = RSA.Create();
        publicOnly.ImportParameters(key.ExportParameters(includePrivateParameters: false));
        var valid = publicOnly.VerifyData(
            Encoding.UTF8.GetBytes(signed.StringToSign),
            Convert.FromBase64String(signed.Signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.True(valid);
    }

    [Fact]
    public void CreditNotesAreSignedAsNegativeAmounts()
    {
        using var key = Key();
        var signed = OfflineSigner.Sign(
            Request(new[] { new ParsedItem("Returned coat", 1m, 100m, 100m, "STANDARD", 0.15m) },
                receiptType: "CreditNote"),
            key);

        Assert.Equal(-100.00m, signed.ReceiptTotal);
        Assert.Equal(-13.04m, signed.TaxTotal);
        var tax = Assert.Single(signed.Taxes);
        Assert.Equal(-13.04m, tax.TaxAmount);
        Assert.Equal(-100.00m, tax.SalesAmountWithTax);
        Assert.Contains("A15.00-1304-10000", signed.StringToSign);
    }

    [Fact]
    public void DeviceIdIsNormalizedLikeTheServer()
    {
        using var key = Key();
        var signed = OfflineSigner.Sign(Request(deviceId: "0001234567890"), key);

        Assert.StartsWith("1234567890FISCALINVOICE", signed.StringToSign);
        Assert.Equal("1234567890", signed.DeviceId);
    }
}
