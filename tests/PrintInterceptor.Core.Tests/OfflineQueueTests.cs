using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
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
