using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.State;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class JobLedgerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "printinterceptor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void ReprintIsTrackedAndSurvivesReload()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);
        ledger.Claim("hash-b", "INV-2", "20261029", null, 5m, "USD");
        ledger.MarkFiscalised("hash-b", new FiscalizeResponse
        {
            Success = true,
            Receipt = new FiscalReceipt { InvoiceNo = "INV-2" }
        });

        ledger.MarkReprinted("hash-b", "Test printer");
        ledger.MarkReprinted("hash-b", "Test printer");

        var reloaded = new JobLedger(_directory, NullLog.Instance);
        var entry = reloaded.Get("hash-b");

        Assert.NotNull(entry);
        Assert.Equal(2, entry!.ReprintCount);
        Assert.Equal("Reprinted", entry.PrintStatus);
        Assert.Single(reloaded.All());
    }

    [Fact]
    public void ClaimIsExclusiveAndSurvivesReload()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD"));
        Assert.False(ledger.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD"));

        ledger.MarkFiscalised("hash-a", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "ABC123",
            ReceiptNumber = 77,
            Receipt = new FiscalReceipt
            {
                InvoiceNo = "INV-1",
                ReceiptCounter = 3,
                ReceiptGlobalNo = 77,
                FiscalDayNo = 5
            }
        });

        var reloaded = new JobLedger(_directory, NullLog.Instance);
        var entry = reloaded.FindBlockingByHash("hash-a");

        Assert.NotNull(entry);
        Assert.Equal(LedgerStatus.Fiscalised, entry!.Status);
        Assert.Equal("ABC123", entry.FiscalCode);
        Assert.Equal(77, entry.ReceiptNumber);
        Assert.False(reloaded.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD"));
    }

    [Fact]
    public void SameInvoiceSameDayIsBlockedButNextDayIsAllowed()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-1", "INV-5", "20261029", null, 10m, "USD"));
        Assert.False(ledger.Claim("hash-2", "INV-5", "20261029", null, 11m, "USD"));
        Assert.True(ledger.Claim("hash-3", "INV-5", "20261030", null, 12m, "USD"));
    }

    [Fact]
    public void NeedsReviewDoesNotBlockARetry()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        ledger.RecordNeedsReview("hash-r", null, "20261029", "No line items", null);
        Assert.True(ledger.Claim("hash-r", null, "20261029", null, 5m, "USD"));
    }

    [Fact]
    public void AmbiguousFailureBlocksRetry()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-x", "INV-9", "20261029", null, 5m, "USD"));
        ledger.MarkFailure("hash-x", LedgerStatus.AmbiguousFailure, "timeout");

        Assert.NotNull(ledger.FindBlockingByHash("hash-x"));
        Assert.False(ledger.Claim("hash-x", "INV-9", "20261029", null, 5m, "USD"));
    }

    [Fact]
    public void DuplicateAttemptsAreCountedLoggedAndSurviveReload()
    {
        var log = new RecordingLog();
        var ledger = new JobLedger(_directory, log);
        Assert.True(ledger.Claim("hash-d", "INV-DUP", "20261031", null, 25m, "USD"));
        ledger.MarkFiscalised("hash-d", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "FISCAL-1",
            ReceiptNumber = 101,
            Receipt = new FiscalReceipt { InvoiceNo = "INV-DUP" }
        });

        ledger.RecordDuplicateAttempt("hash-d", "hash-d-second-print");

        var reloaded = new JobLedger(_directory, NullLog.Instance);
        var entry = reloaded.Get("hash-d");
        Assert.NotNull(entry);
        Assert.Equal(1, entry!.DuplicateAttemptCount);
        Assert.Equal("hash-d-second-print", entry.LastDuplicateAttemptHash);
        Assert.NotNull(entry.LastDuplicateAttemptAt);
        Assert.False(reloaded.Claim("hash-d-second-print", "INV-DUP", "20261031", null, 25m, "USD"));
        Assert.Single(reloaded.All());
        Assert.Contains(log.Warnings, message =>
            message.Contains("DUPLICATE PRINT SUPPRESSED", StringComparison.Ordinal) &&
            message.Contains("INV-DUP", StringComparison.Ordinal) &&
            message.Contains("No second fiscal receipt was created", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateAttemptIsWrittenToTheDailyLogFile()
    {
        using var log = new FileLog(_directory, echoToConsole: false);
        var ledger = new JobLedger(_directory, log);
        Assert.True(ledger.Claim("hash-log", "INV-LOG", "20261031", null, 10m, "USD"));

        ledger.RecordDuplicateAttempt("hash-log", "hash-log-reprint");

        var path = Assert.Single(Directory.GetFiles(_directory, "printinterceptor-*.log"));
        var contents = File.ReadAllText(path);
        Assert.Contains("DUPLICATE PRINT SUPPRESSED", contents, StringComparison.Ordinal);
        Assert.Contains("INV-LOG", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentClaimsForTheSameInvoiceAllowOnlyOne()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);
        var claimed = new bool[32];

        Parallel.For(0, claimed.Length, index =>
        {
            claimed[index] = ledger.Claim(
                $"hash-concurrent-{index}", "INV-CONCURRENT", "20261031", null, 10m, "USD");
        });

        Assert.Equal(1, claimed.Count(value => value));
        Assert.Single(ledger.All());
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = new();

        public void Info(string message) { }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void CorruptLedgerRefusesToStart()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "ledger.json"), "{ not json");

        Assert.Throws<InvalidOperationException>(() => new JobLedger(_directory, NullLog.Instance));
    }

    [Fact]
    public void GetFailureSnapshotCountsRejectionsAmbiguousAndPrintFailures()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        // Rejected: a fiscalisation the server refused outright.
        Assert.True(ledger.Claim("hash-rejected", "INV-REJ", "20261031", null, 10m, "USD"));
        ledger.MarkFailure("hash-rejected", LedgerStatus.Rejected, "Invoice already exists at ZIMRA.");

        // Ambiguous: the client could not tell whether the server accepted it.
        Assert.True(ledger.Claim("hash-ambiguous", "INV-AMB", "20261031", null, 20m, "USD"));
        ledger.MarkFailure("hash-ambiguous", LedgerStatus.AmbiguousFailure, "Request timed out.");

        // Print failure after a successful fiscalisation.
        Assert.True(ledger.Claim("hash-print", "INV-PRT", "20261031", null, 30m, "USD"));
        ledger.MarkFiscalised("hash-print", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "FISCAL-PRT",
            ReceiptNumber = 201,
            Receipt = new FiscalReceipt { InvoiceNo = "INV-PRT" }
        });
        ledger.SetPrintResult("hash-print", "Failed", "Printer offline.");

        // Successful jobs must not appear in any failure bucket.
        Assert.True(ledger.Claim("hash-clean", "INV-OK", "20261031", null, 40m, "USD"));
        ledger.MarkFiscalised("hash-clean", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "FISCAL-OK",
            ReceiptNumber = 202,
            Receipt = new FiscalReceipt { InvoiceNo = "INV-OK" }
        });
        ledger.SetPrintResult("hash-clean", "Printed", null);

        var snapshot = ledger.GetFailureSnapshot();

        Assert.Equal(1, snapshot.Rejected);
        Assert.Equal(1, snapshot.Ambiguous);
        Assert.Equal(1, snapshot.PrintFailed);
        Assert.Equal("INV-REJ", snapshot.LatestRejected?.InvoiceNumber);
        Assert.Equal("Invoice already exists at ZIMRA.", snapshot.LatestRejected?.Error);
        Assert.Equal("INV-AMB", snapshot.LatestAmbiguous?.InvoiceNumber);
        Assert.Equal("INV-PRT", snapshot.LatestPrintFailure?.InvoiceNumber);
        Assert.Equal("Printer offline.", snapshot.LatestPrintFailure?.PrintError);
    }
}
