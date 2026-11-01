using System.Diagnostics;
using System.Windows.Forms;

namespace PrintInterceptor;

/// <summary>
/// Opens a file or folder with the shell, reporting a failure in a message box instead
/// of throwing. Shared by the tray menu and the settings window.
/// </summary>
internal static class ShellOpen
{
    public static void Path(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PrintInterceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Creates the folder first, so opening an empty one never fails.</summary>
    public static void Folder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "PrintInterceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Path(path);
    }
}
