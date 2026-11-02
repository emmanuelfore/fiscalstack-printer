using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Storage;
using Xunit;

namespace PrintInterceptor.Core.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pi-jobs-" + Guid.NewGuid().ToString("N"));

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
    public void SavesDecodedTextAndTheOriginalPdf()
    {
        var store = CreateStore();
        var raw = "%PDF-1.7\nreceived"u8.ToArray();
        var decoded = new DecodedJob(true, JobFormat.Pdf, "decoded invoice", null, null);

        var textPath = store.Save("invoice-hash", decoded, raw);

        Assert.NotNull(textPath);
        Assert.Equal("decoded invoice", File.ReadAllText(textPath!));
        var pdfPath = Path.Combine(Path.GetDirectoryName(textPath!)!, "invoice-hash.pdf");
        Assert.True(File.Exists(pdfPath));
        Assert.Equal(raw, File.ReadAllBytes(pdfPath));
    }

    [Fact]
    public void KeepsTheOriginalFileWhenDecodingFails()
    {
        var store = CreateStore();
        var raw = "%PDF-1.7\nbroken"u8.ToArray();

        var path = store.SaveUndecodable(raw, JobFormat.Pdf);

        Assert.NotNull(path);
        Assert.EndsWith(".pdf", path, StringComparison.Ordinal);
        Assert.Equal(raw, File.ReadAllBytes(path!));
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(Path.GetDirectoryName(path!)!));
    }

    [Fact]
    public void DisabledRetentionDoesNotWriteReceivedFiles()
    {
        var config = new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory, RetainJobs = false }
        };
        var store = new JobStore(config, NullLog.Instance);

        Assert.Null(store.SaveUndecodable("received"u8.ToArray(), JobFormat.Text));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    public void KeepsTheBytesUsedToStampTheOriginalInvoice()
    {
        var store = CreateStore();
        var raw = "%!PS-Adobe-3.0\ninvoice"u8.ToArray();

        var path = store.SaveOriginalCopy("stamp-hash", raw, JobFormat.PostScript);

        Assert.NotNull(path);
        Assert.EndsWith("stamp-hash.ps", path, StringComparison.Ordinal);
        Assert.Equal("original", Path.GetFileName(Path.GetDirectoryName(path)!));
        Assert.Equal(raw, File.ReadAllBytes(path!));

        // A reprint resolves the same path; the POS's bytes are never overwritten.
        var again = store.SaveOriginalCopy("stamp-hash", "different"u8.ToArray(), JobFormat.PostScript);
        Assert.Equal(path, again);
        Assert.Equal(raw, File.ReadAllBytes(again!));
    }

    [Fact]
    public void DisabledRetentionLeavesNothingToStamp()
    {
        var config = new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory, RetainJobs = false }
        };
        var store = new JobStore(config, NullLog.Instance);

        Assert.Null(store.SaveOriginalCopy("stamp-hash", "received"u8.ToArray(), JobFormat.Text));
    }

    [Fact]
    public void KeepsTheExactRequestBodyBesideTheReceiptItWasBuiltFrom()
    {
        const string body = """{"items":[{"name":"Bread Loaf","quantity":1,"unitPrice":2.5}],"invoiceNumber":"INV-1001"}""";
        var store = CreateStore();
        var sourceFile = store.Save(
            "req-hash", new DecodedJob(true, JobFormat.Text, "Total: 2.50", null, null), "Total: 2.50"u8.ToArray());

        var requestPath = store.SaveRequest(sourceFile, body);

        Assert.NotNull(requestPath);
        Assert.Equal(JobStore.RequestPath(sourceFile!), requestPath);
        Assert.Equal(body, File.ReadAllText(requestPath!));

        // A reconciliation reads the file back through the receipt it belongs to, not the path.
        Assert.Equal(body, store.LoadRequest(sourceFile));

        // A second store on the same folder proves the body is on disk, not in memory.
        Assert.Equal(body, CreateStore().LoadRequest(sourceFile));
    }

    [Fact]
    public void ThereIsNoRequestToReplayForAJobWhoseTextWasNeverArchived()
    {
        var store = CreateStore();

        Assert.Null(store.SaveRequest(null, "{}"));
        Assert.Null(store.LoadRequest(null));
        Assert.Null(store.LoadText(null));
    }

    [Fact]
    public void DisabledRetentionLeavesNothingToReplay()
    {
        var config = new InterceptorConfig
        {
            Storage = new StorageConfig { DataDirectory = _directory, RetainJobs = false }
        };
        var store = new JobStore(config, NullLog.Instance);

        Assert.Null(store.SaveRequest(Path.Combine(_directory, "today", "hash.txt"), "{}"));
    }

    [Fact]
    public void TheArchivedPdfIsFoundAgainFromItsReceiptText()
    {
        var store = CreateStore();
        var raw = "%PDF-1.7\ninvoice"u8.ToArray();
        var sourceFile = store.Save(
            "pdf-hash", new DecodedJob(true, JobFormat.Pdf, "decoded invoice", null, null), raw);

        Assert.NotNull(store.SaveOriginalCopy("pdf-hash", raw, JobFormat.Pdf));

        Assert.Equal(JobFormat.Pdf, store.RetainedFormat(sourceFile));
        Assert.EndsWith("original/pdf-hash.pdf", store.RetainedOriginalPath(sourceFile), StringComparison.Ordinal);
    }

    [Fact]
    public void ATextReceiptHasNoPostScriptOrPdfBehindIt()
    {
        var store = CreateStore();
        var sourceFile = store.Save(
            "txt-hash", new DecodedJob(true, JobFormat.Text, "Total: 5.00", null, null), "Total: 5.00"u8.ToArray());

        Assert.Equal(JobFormat.Text, store.RetainedFormat(sourceFile));
        Assert.Null(store.RetainedOriginalPath(sourceFile));
    }

    private JobStore CreateStore() => new(
        new InterceptorConfig { Storage = new StorageConfig { DataDirectory = _directory } },
        NullLog.Instance);
}
