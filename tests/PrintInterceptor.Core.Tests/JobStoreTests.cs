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

    private JobStore CreateStore() => new(
        new InterceptorConfig { Storage = new StorageConfig { DataDirectory = _directory } },
        NullLog.Instance);
}
