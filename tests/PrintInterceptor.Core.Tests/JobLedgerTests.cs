using System.Text.RegularExpressions;
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
        ledger.Claim("hash-b", "INV-2", "20261029", null, 5m, "USD", "FiscalInvoice");
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

        Assert.True(ledger.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD", "FiscalInvoice"));
        Assert.False(ledger.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD", "FiscalInvoice"));

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
        Assert.False(reloaded.Claim("hash-a", "INV-1", "20261029", null, 10m, "USD", "FiscalInvoice"));
    }

    [Fact]
    public void SameInvoiceSameDayIsBlockedButNextDayIsAllowed()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-1", "INV-5", "20261029", null, 10m, "USD", "FiscalInvoice"));
        Assert.False(ledger.Claim("hash-2", "INV-5", "20261029", null, 11m, "USD", "FiscalInvoice"));
        Assert.True(ledger.Claim("hash-3", "INV-5", "20261030", null, 12m, "USD", "FiscalInvoice"));
    }

    [Fact]
    public void AReturnOnTheOriginalsNumberIsNotSuppressedAsADuplicateSale()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-sale", "INV-7", "20261029", null, 100m, "USD", "FiscalInvoice"));
        ledger.MarkFiscalised("hash-sale", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "SALE-CODE",
            Receipt = new FiscalReceipt { InvoiceNo = "INV-7" }
        });

        // The till prints the original's number on the return slip; the two are different
        // documents, so the sale must not be able to swallow the return.
        Assert.True(ledger.Claim("hash-return", "INV-7", "20261029", null, 25m, "USD", "CreditNote"));

        Assert.Equal("hash-sale", ledger.FindBlockingByInvoice("INV-7", "20261029", "FiscalInvoice")?.Hash);
        Assert.Equal("hash-return", ledger.FindBlockingByInvoice("INV-7", "20261029", "CreditNote")?.Hash);

        Assert.False(ledger.Claim("hash-sale-again", "INV-7", "20261029", null, 60m, "USD", "FiscalInvoice"));
        Assert.False(ledger.Claim("hash-sale-null-type", "INV-7", "20261029", null, 60m, "USD", null));
    }

    [Fact]
    public void AnEntryWrittenBeforeTypesExistedBlocksEveryKind()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);
        Assert.True(ledger.Claim("hash-old", "INV-OLD", "20261029", null, 10m, "USD", "FiscalInvoice"));
        ledger.MarkFiscalised("hash-old", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "OLD-CODE",
            Receipt = new FiscalReceipt { InvoiceNo = "INV-OLD" }
        });

        // An absent key and a null one load the same way, so blanking the value
        // reproduces an entry written before the distinction existed.
        var path = Path.Combine(_directory, "ledger.json");
        File.WriteAllText(
            path,
            Regex.Replace(
                File.ReadAllText(path), @"""transactionType"":\s*""[^""]*""", @"""transactionType"": null"));

        var reloaded = new JobLedger(_directory, NullLog.Instance);
        Assert.False(reloaded.Claim("hash-new-return", "INV-OLD", "20261029", null, 4m, "USD", "CreditNote"));
        Assert.False(reloaded.Claim("hash-new-sale", "INV-OLD", "20261029", null, 4m, "USD", "FiscalInvoice"));
    }

    [Fact]
    public void NeedsReviewDoesNotBlockARetry()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        ledger.RecordNeedsReview("hash-r", null, "20261029", "No line items", null);
        Assert.True(ledger.Claim("hash-r", null, "20261029", null, 5m, "USD", "FiscalInvoice"));
    }

    [Fact]
    public void AmbiguousFailureBlocksRetry()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);

        Assert.True(ledger.Claim("hash-x", "INV-9", "20261029", null, 5m, "USD", "FiscalInvoice"));
        ledger.MarkFailure("hash-x", LedgerStatus.AmbiguousFailure, "timeout");

        Assert.NotNull(ledger.FindBlockingByHash("hash-x"));
        Assert.False(ledger.Claim("hash-x", "INV-9", "20261029", null, 5m, "USD", "FiscalInvoice"));
    }

    [Fact]
    public void DuplicateAttemptsAreCountedLoggedAndSurviveReload()
    {
        var log = new RecordingLog();
        var ledger = new JobLedger(_directory, log);
        Assert.True(ledger.Claim("hash-d", "INV-DUP", "20261031", null, 25m, "USD", "FiscalInvoice"));
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
        Assert.False(reloaded.Claim("hash-d-second-print", "INV-DUP", "20261031", null, 25m, "USD", "FiscalInvoice"));
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
        Assert.True(ledger.Claim("hash-log", "INV-LOG", "20261031", null, 10m, "USD", "FiscalInvoice"));

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
                $"hash-concurrent-{index}", "INV-CONCURRENT", "20261031", null, 10m, "USD", "FiscalInvoice");
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
        Assert.True(ledger.Claim("hash-rejected", "INV-REJ", "20261031", null, 10m, "USD", "FiscalInvoice"));
        ledger.MarkFailure("hash-rejected", LedgerStatus.Rejected, "Invoice already exists at ZIMRA.");

        // Ambiguous: the client could not tell whether the server accepted it.
        Assert.True(ledger.Claim("hash-ambiguous", "INV-AMB", "20261031", null, 20m, "USD", "FiscalInvoice"));
        ledger.MarkFailure("hash-ambiguous", LedgerStatus.AmbiguousFailure, "Request timed out.");

        // Print failure after a successful fiscalisation.
        Assert.True(ledger.Claim("hash-print", "INV-PRT", "20261031", null, 30m, "USD", "FiscalInvoice"));
        ledger.MarkFiscalised("hash-print", new FiscalizeResponse
        {
            Success = true,
            FiscalCode = "FISCAL-PRT",
            ReceiptNumber = 201,
            Receipt = new FiscalReceipt { InvoiceNo = "INV-PRT" }
        });
        ledger.SetPrintResult("hash-print", "Failed", "Printer offline.");

        // Successful jobs must not appear in any failure bucket.
        Assert.True(ledger.Claim("hash-clean", "INV-OK", "20261031", null, 40m, "USD", "FiscalInvoice"));
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

    [Fact]
    public void AmbiguousJobsAreOfferedForReconciliationOldestFirst()
    {
        var now = DateTimeOffset.Now;
        var ledger = LedgerWith(
            ("new", "AmbiguousFailure", now.AddMinutes(-5), 0),
            ("old", "AmbiguousFailure", now.AddMinutes(-40), 0),
            ("done", "Fiscalised", now.AddMinutes(-60), 0));

        var pending = ledger.PendingReconcile(10, 3, now, TimeSpan.FromMinutes(10));

        Assert.Equal(new[] { "old", "new" }, pending.Select(entry => entry.Hash));
    }

    [Fact]
    public void AJobInTheMiddleOfFiscalisingIsNotUnknownYet()
    {
        // InFlight only means "outcome unknown" once the request itself could not still be running.
        var now = DateTimeOffset.Now;
        var ledger = LedgerWith(
            ("live", "InFlight", now.AddMinutes(-1), 0),
            ("crashed", "InFlight", now.AddMinutes(-11), 0));

        var pending = ledger.PendingReconcile(10, 3, now, TimeSpan.FromMinutes(10));

        Assert.Equal("crashed", Assert.Single(pending).Hash);
    }

    [Fact]
    public void TheAutomaticPassStopsAtTheBudgetButTheOperatorDoesNot()
    {
        var now = DateTimeOffset.Now;
        var ledger = LedgerWith(("tired", "AmbiguousFailure", now.AddMinutes(-20), 3));

        Assert.Empty(ledger.PendingReconcile(10, 3, now, TimeSpan.FromMinutes(10)));
        Assert.Single(ledger.PendingReconcile(10, int.MaxValue, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void AttemptCountingSurvivesReloadAndAFreshClaimResetsIt()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);
        ledger.Claim("hash-x", "INV-X", "20261029", null, 5m, "USD", "FiscalInvoice");
        ledger.MarkFailure("hash-x", LedgerStatus.AmbiguousFailure, "The request timed out after 30s.");

        ledger.RecordReconcileAttempt("hash-x");
        ledger.RecordReconcileAttempt("hash-x");

        var reloaded = new JobLedger(_directory, NullLog.Instance);
        Assert.Equal(2, reloaded.Get("hash-x")!.ReconcileAttempts);
        Assert.NotNull(reloaded.Get("hash-x")!.LastReconcileAt);

        // The gateway refused the replay, the till sold it again under a new number, so the
        // budget belongs to that attempt rather than to the one that just ended.
        reloaded.MarkFailure("hash-x", LedgerStatus.Rejected, "Invoice number already exists.");
        Assert.True(reloaded.Claim("hash-x", "INV-X2", "20261029", null, 5m, "USD", "FiscalInvoice"));

        var entry = reloaded.Get("hash-x");
        Assert.Equal(0, entry!.ReconcileAttempts);
        Assert.Null(entry.LastReconcileAt);
    }

    [Fact]
    public void AdoptingTheServersProofMarksTheJobFiscalised()
    {
        var ledger = new JobLedger(_directory, NullLog.Instance);
        ledger.Claim("hash-a", "INV-A", "20261029", "jobs/2026-10-29/hash-a.txt", 5m, "USD", "FiscalInvoice");
        ledger.MarkFailure("hash-a", LedgerStatus.AmbiguousFailure, "The request timed out after 30s.");
        ledger.RecordReconcileAttempt("hash-a");

        ledger.MarkFiscalisedFromServer(
            "hash-a",
            new InvoiceRecord(
                41,
                "INV-A",
                "FISCALISED",
                "FiscalInvoice",
                "USD",
                5.00m,
                "FC-41",
                "https://verify.test/FC-41",
                77,
                3,
                5,
                "2026-10-29"));

        var entry = new JobLedger(_directory, NullLog.Instance).Get("hash-a");

        Assert.Equal(LedgerStatus.Fiscalised, entry!.Status);
        Assert.Equal("FC-41", entry.FiscalCode);
        Assert.Equal("https://verify.test/FC-41", entry.QrCode);
        Assert.Equal(77, entry.ReceiptNumber);
        Assert.Equal(77, entry.ReceiptGlobalNo);
        Assert.Equal(3, entry.ReceiptCounter);
        Assert.Equal(5, entry.FiscalDayNo);
        Assert.Null(entry.Error);
        Assert.Equal(1, entry.ReconcileAttempts);

        // The list read carries no verification code, and nothing here invents one.
        Assert.Null(entry.VerificationCode);
    }

    /// <summary>
    /// A ledger as it would be on disk, with the timestamps the passes are judged against.
    /// </summary>
    private JobLedger LedgerWith(params (string Hash, string Status, DateTimeOffset UpdatedAt, int Attempts)[] rows)
    {
        var entries = rows.Select(row =>
            $$"""
            "{{row.Hash}}": {
              "hash": "{{row.Hash}}",
              "invoiceNumber": "INV-{{row.Hash}}",
              "businessDate": "20261029",
              "transactionType": "FiscalInvoice",
              "status": "{{row.Status}}",
              "createdAt": "{{row.UpdatedAt.AddMinutes(-90).ToString("O")}}",
              "updatedAt": "{{row.UpdatedAt.ToString("O")}}",
              "total": 5.0,
              "currency": "USD",
              "reconcileAttempts": {{row.Attempts}}
            }
            """);

        var json = "{\"version\": 1, \"entries\": { " + string.Join(",", entries) + " } }";

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "ledger.json"), json);
        return new JobLedger(_directory, NullLog.Instance);
    }
}
