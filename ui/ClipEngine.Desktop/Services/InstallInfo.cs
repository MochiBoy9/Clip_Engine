using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipEngine.Desktop.Services;

// When Clip Engine came from the installer (installer/ClipEngine.iss), Windows lists it under Apps & features with
// the version it was installed at. The app updates itself in place, so each start puts the running version there.
[SupportedOSPlatform("windows")]
internal static class InstallInfo
{
    // The installer's AppId, as Inno Setup names its uninstall key.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1B8E2A-4C7D-4E59-9A3B-CE1A2D7F5B10}_is1";

    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is null) return;
            var version = UpdateFeed.CurrentVersion.ToString(3);
            if (key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version);
            key.SetValue("DisplayName", "Clip Engine");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }
}
