using System.Security.Cryptography;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.State;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class FiscalChainStoreTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "pi-chain-" + Guid.NewGuid().ToString("N"));

    private static OfflineSignRequest Request(FiscalChainSnapshot snapshot) => new()
    {
        DeviceId = "1234567890",
        ReceiptType = "FiscalInvoice",
        Currency = "USD",
        ReceiptDate = "2026-10-30T10:15:00",
        ReceiptCounter = snapshot.ReceiptCounter + 1,
        ReceiptGlobalNo = snapshot.ReceiptGlobalNo + 1,
        FiscalDayNo = snapshot.FiscalDayNo,
        PreviousHash = snapshot.LastHash,
        TaxInclusive = true,
        StandardTaxPercent = 15m,
        Taxes = OfflineConfig.DefaultTaxes(),
        QrBaseUrl = "https://fdms.zimra.co.zw/",
        Items = new[] { new ParsedItem("Bread", 1m, 10m, 10m, "STANDARD", 0.15m) }
    };

    [Fact]
    public void SignsAndPersistsTheNextCounters()
    {
        var directory = TempDirectory();
        try
        {
            var store = new FiscalChainStore(directory, NullLog.Instance);
            Assert.False(store.TryGetCurrent("1234567890", out _, out _));

            store.SeedFromServer("1234567890", new OfflineState
            {
                DeviceId = "1234567890",
                LastFiscalHash = "PREVIOUS",
                CurrentFiscalDayNo = 12,
                DailyReceiptCount = 34,
                LastReceiptGlobalNo = 1055
            });

            using var key = RSA.Create(2048);
            var issued = store.TrySignNext(
                "1234567890",
                snapshot => OfflineSigner.Sign(Request(snapshot), key),
                out var signed,
                out var error);

            Assert.True(issued, error);
            Assert.Equal(35, signed.ReceiptCounter);
            Assert.Equal(1056, signed.ReceiptGlobalNo);
            Assert.Equal("PREVIOUS", signed.PreviousHash);

            var reloaded = new FiscalChainStore(directory, NullLog.Instance);
            Assert.True(reloaded.TryGetCurrent("1234567890", out var current, out _));
            Assert.Equal(35, current.ReceiptCounter);
            Assert.Equal(1056, current.ReceiptGlobalNo);
            Assert.Equal(signed.Hash, current.LastHash);

            Assert.False(reloaded.TryGetCurrent("9999999999", out _, out var mismatch));
            Assert.Contains("belongs to device", mismatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OnlineReceiptsAdvanceTheChainButNeverRewindIt()
    {
        var directory = TempDirectory();
        try
        {
            var store = new FiscalChainStore(directory, NullLog.Instance);
            store.SeedFromServer("1234567890", new OfflineState
            {
                LastFiscalHash = "H1",
                CurrentFiscalDayNo = 3,
                DailyReceiptCount = 7,
                LastReceiptGlobalNo = 100
            });

            store.RecordOnlineSuccess("1234567890", new FiscalizeResponse
            {
                FiscalCode = "H2",
                ReceiptNumber = 101,
                Receipt = new FiscalReceipt
                {
                    ReceiptCounter = 8,
                    ReceiptGlobalNo = 101,
                    FiscalDayNo = 3
                }
            });

            Assert.True(store.TryGetCurrent("1234567890", out var current, out _));
            Assert.Equal(8, current.ReceiptCounter);
            Assert.Equal(101, current.ReceiptGlobalNo);
            Assert.Equal("H2", current.LastHash);

            store.RecordOnlineSuccess("1234567890", new FiscalizeResponse
            {
                FiscalCode = "H0",
                ReceiptNumber = 99,
                Receipt = new FiscalReceipt { ReceiptCounter = 6, ReceiptGlobalNo = 99, FiscalDayNo = 3 }
            });

            Assert.True(store.TryGetCurrent("1234567890", out current, out _));
            Assert.Equal(101, current.ReceiptGlobalNo);
            Assert.Equal("H2", current.LastHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
