using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipEngine.Desktop.Services;

// clipengine:// links open Clip Engine, like the Open in Clip Engine button on a squad invite's
// web page (clipengine://squad/join/<code>). Registered for this Windows account each start, so
// it follows the app when it moves.
[SupportedOSPlatform("windows")]
internal static class UrlProtocol
{
    public const string Scheme = "clipengine";

    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", "URL:Clip Engine");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            var wanted = $"\"{exe}\" \"%1\"";
            if (command.GetValue("") as string != wanted) command.SetValue("", wanted);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }
}
