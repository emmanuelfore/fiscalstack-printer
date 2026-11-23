using System.Security.Cryptography;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Parsing;
using PrintInterceptor.Core.State;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public class OfflineQueueTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "pi-queue-" + Guid.NewGuid().ToString("N"));

    private static OfflineQueueItem Item(string hash, DateTimeOffset createdAt) => new()
    {
        JobHash = hash,
        CreatedAt = createdAt,
        Request = new FiscalizeRequest { Currency = "USD" },
        ReceiptCounter = 1,
        ReceiptGlobalNo = 10,
        FiscalDayNo = 1
    };

    [Fact]
    public void PeeksOldestFirstAndRemovesOnlyThatReceipt()
    {
        var directory = TempDirectory();
        try
        {
            var queue = new OfflineQueue(directory, NullLog.Instance);
            var older = Item("aaaa", new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero));
            var newer = Item("bbbb", new DateTimeOffset(2026, 10, 30, 10, 5, 0, TimeSpan.Zero));

            Assert.True(queue.TryEnqueue(older, out _));
            Assert.True(queue.TryEnqueue(newer, out _));
            Assert.Equal(2, queue.Count);

            Assert.True(queue.TryPeek(out var first, out var error));
            Assert.Equal("", error);
            Assert.Equal("aaaa", first.JobHash);

            queue.Remove(first);
            Assert.True(queue.TryPeek(out var second, out _));
            Assert.Equal("bbbb", second.JobHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Signing commits the chain before the pipeline can enqueue. If the queue write fails,
    /// the counter is already spent: the signed receipt must survive verbatim and be put back
    /// at the head of the queue, otherwise the next receipt would skip a ZIMRA number.
    /// </summary>
    [Fact]
    public void AFailedEnqueueKeepsTheSignedReceiptAndDoesNotSkipItsCounter()
    {
        var directory = TempDirectory();
        try
        {
            const string deviceId = "1234567890";
            var log = NullLog.Instance;
            var chain = new FiscalChainStore(directory, log);
            chain.SeedFromServer(deviceId, new OfflineState
            {
                DeviceId = deviceId,
                LastFiscalHash = "PREVIOUS",
                CurrentFiscalDayNo = 5,
                DailyReceiptCount = 3,
                LastReceiptGlobalNo = 100
            });

            using var key = RSA.Create(2048);
            bool Sign(out OfflineSignedReceipt signed) =>
                chain.TrySignNext(
                    deviceId,
                    s => OfflineSigner.Sign(SignRequest(s, deviceId), key),
                    out signed,
                    out _);

            Assert.True(Sign(out var signed));

            var queue = new OfflineQueue(directory, log) { ChainProvider = () => chain.Current };
            var item = Item("offline-1", DateTimeOffset.Now);
            item.Request = new FiscalizeRequest
            {
                InvoiceNumber = "INV-OFFLINE",
                Currency = "USD",
                OfflineSignature = signed.Signature
            };
            item.Signature = signed.Signature;
            item.SignedHash = signed.Hash;
            item.ReceiptCounter = signed.ReceiptCounter;
            item.ReceiptGlobalNo = signed.ReceiptGlobalNo;
            item.FiscalDayNo = signed.FiscalDayNo;

            // Simulate the queue write failing (the directory is gone) after the chain advanced.
            Directory.Delete(queue.DirectoryPath);
            Assert.False(queue.TryEnqueue(item, out var queueError));
            Assert.NotEmpty(queueError);

            Assert.True(chain.TryGetCurrent(deviceId, out var current, out _));
            Assert.Equal(signed.ReceiptGlobalNo, current.ReceiptGlobalNo);
            Assert.Equal(signed.ReceiptCounter, current.ReceiptCounter);

            var recoveryFiles = Directory.GetFiles(queue.RecoveryDirectoryPath, "*.json");
            Assert.Single(recoveryFiles);

            Directory.CreateDirectory(queue.DirectoryPath);
            Assert.Equal(1, queue.RecoverPending());
            Assert.Empty(Directory.GetFiles(queue.RecoveryDirectoryPath, "*.json"));

            Assert.True(queue.TryPeek(out var recovered, out _));
            Assert.Equal("offline-1", recovered.JobHash);
            Assert.Equal(signed.Signature, recovered.Signature);
            Assert.Equal(signed.Hash, recovered.SignedHash);
            Assert.Equal(signed.ReceiptGlobalNo, recovered.ReceiptGlobalNo);
            Assert.Equal(signed.ReceiptCounter, recovered.ReceiptCounter);

            // The very next signature uses the next counter: nothing was burned or reused.
            Assert.True(Sign(out var next));
            Assert.Equal(signed.ReceiptCounter + 1, next.ReceiptCounter);
            Assert.Equal(signed.ReceiptGlobalNo + 1, next.ReceiptGlobalNo);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A recovery file written just before a failed chain commit must not be promoted:
    /// its counter was never spent and would collide with the next offline receipt.
    /// </summary>
    [Fact]
    public void ARecoveryRecordAheadOfTheChainIsDiscarded()
    {
        var directory = TempDirectory();
        try
        {
            const string deviceId = "1234567890";
            var log = NullLog.Instance;
            var chain = new FiscalChainStore(directory, log);
            chain.SeedFromServer(deviceId, new OfflineState
            {
                DeviceId = deviceId,
                LastFiscalHash = "PREVIOUS",
                CurrentFiscalDayNo = 5,
                DailyReceiptCount = 3,
                LastReceiptGlobalNo = 100
            });

            var queue = new OfflineQueue(directory, log) { ChainProvider = () => chain.Current };
            var item = Item("uncommitted", DateTimeOffset.Now);
            item.ReceiptGlobalNo = 101;
            item.ReceiptCounter = 4;

            Assert.True(queue.TrySaveRecovery(item, out _, out _));
            Assert.Equal(0, queue.RecoverPending());
            Assert.Empty(Directory.GetFiles(queue.RecoveryDirectoryPath, "*.json"));
            Assert.Equal(0, queue.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Once the chain owns the signed number the recovery record is promoted, and a
    /// second copy of the same signed receipt is discarded rather than submitted twice.
    /// </summary>
    [Fact]
    public void ACommittedRecoveryRecordIsPromotedOnce()
    {
        var directory = TempDirectory();
        try
        {
            const string deviceId = "1234567890";
            var log = NullLog.Instance;
            var chain = new FiscalChainStore(directory, log);
            chain.SeedFromServer(deviceId, new OfflineState
            {
                DeviceId = deviceId,
                LastFiscalHash = "PREVIOUS",
                CurrentFiscalDayNo = 5,
                DailyReceiptCount = 3,
                LastReceiptGlobalNo = 100
            });

            using var key = RSA.Create(2048);
            Assert.True(chain.TrySignNext(
                deviceId,
                s => OfflineSigner.Sign(SignRequest(s, deviceId), key),
                out var signed,
                out _));

            var queue = new OfflineQueue(directory, log) { ChainProvider = () => chain.Current };
            var item = Item("committed", DateTimeOffset.Now);
            item.Signature = signed.Signature;
            item.SignedHash = signed.Hash;
            item.ReceiptCounter = signed.ReceiptCounter;
            item.ReceiptGlobalNo = signed.ReceiptGlobalNo;
            item.FiscalDayNo = signed.FiscalDayNo;

            Assert.True(queue.TrySaveRecovery(item, out _, out _));
            Assert.Equal(1, queue.RecoverPending());
            Assert.Equal(1, queue.Count);

            Assert.True(queue.TrySaveRecovery(item, out _, out _));
            Assert.Equal(0, queue.RecoverPending());
            Assert.Equal(1, queue.Count);
            Assert.True(queue.TryPeek(out var queued, out _));
            Assert.Equal(signed.ReceiptGlobalNo, queued.ReceiptGlobalNo);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnreadablePauseMarkerFailsClosed()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "offline-sync-pause.json"), "{ not json");

            var queue = new OfflineQueue(directory, NullLog.Instance);

            Assert.True(queue.IsPaused);
            Assert.Contains("unreadable", queue.CurrentPause!.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OfflineSignRequest SignRequest(FiscalChainSnapshot snapshot, string deviceId) => new()
    {
        DeviceId = deviceId,
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
    public void CorruptQueueFileFailsClosed()
    {
        var directory = TempDirectory();
        try
        {
            var queue = new OfflineQueue(directory, NullLog.Instance);
            File.WriteAllText(Path.Combine(queue.DirectoryPath, "20261030100000000-broken.json"), "{ not json");

            Assert.False(queue.TryPeek(out _, out var error));
            Assert.Contains("unreadable", error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
