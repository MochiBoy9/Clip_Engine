using System.Diagnostics;

namespace ClipEngine.Desktop.Services;

internal static class Shell
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    public static void ShowInFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        else
        {
            Open(Path.GetDirectoryName(path) ?? path);
        }
    }

    // Moves the file to the Recycle Bin on Windows so a mistaken delete can be undone.
    public static void Delete(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            File.Delete(path);
        }
    }
}
