using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrintInterceptor.Core.Printing;

/// <summary>
/// Sends bytes straight to a Windows print queue with the RAW data type, so a
/// thermal till receives exactly what the POS produced. GDI cannot do this: it
/// re-renders everything through the driver.
/// </summary>
public static class RawPrinterWriter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string DocName;
        public string? OutputFile;
        public string DataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartDocPrinterW")]
    private static extern bool StartDocPrinter(IntPtr handle, int level, ref DocInfo1 info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    /// <summary>
    /// Queues <c>data</c> on <c>printerName</c>. Throws when the queue is unknown or
    /// the spooler refuses any step, so the pipeline records a print failure rather
    /// than reporting a receipt that never reached the customer.
    /// </summary>
    public static void Send(string printerName, byte[] data, string documentName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RAW spooling needs the Windows print spooler.");
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new ArgumentException("A printer name is required for a RAW job.", nameof(printerName));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("There are no bytes to send.", nameof(data));
        }

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Printer '{printerName}' could not be opened.");
        }

        try
        {
            var info = new DocInfo1
            {
                DocName = documentName,
                OutputFile = null,
                DataType = "RAW"
            };

            if (!StartDocPrinter(handle, 1, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start the RAW job on '{printerName}'.");
            }

            try
            {
                if (!StartPagePrinter(handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start a page on '{printerName}'.");
                }

                try
                {
                    if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"Only {written} of {data.Length} bytes reached '{printerName}'.");
                    }
                }
                finally
                {
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
