using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace ClipEngine.Desktop.Services.Stats;

// Windows shares games' frame timing only with administrators and the Performance Log Users group.
// Allowing it adds this Windows account to that group, once, with Windows' own permission prompt;
// Windows applies it the next time you sign in.
[SupportedOSPlatform("windows")]
internal static class FrameTimingAccess
{
    private const string PerformanceLogUsers = "S-1-5-32-559";

    public static bool Allowed()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(new SecurityIdentifier(PerformanceLogUsers));
    }

    // Asks Windows to add this account to the group. Null when it worked (or the account was in it
    // already), else why not.
    public static async Task<string?> RequestAsync()
    {
        string user;
        using (var identity = WindowsIdentity.GetCurrent()) user = identity.User?.Value ?? "";
        if (user.Length == 0) return "Windows didn't say which account this is.";

        // By SID, so it works whatever language Windows is in.
        var script = "$ErrorActionPreference='Stop'; try { Add-LocalGroupMember -SID '" + PerformanceLogUsers + "' -Member '" + user +
                     "' } catch [Microsoft.PowerShell.Commands.MemberExistsException] { exit 0 } catch { exit 1 }; exit 0";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "Windows didn't start the change.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : "Windows couldn't add this account to Performance Log Users.";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "The Windows prompt was cancelled.";
        }
        catch (Win32Exception e)
        {
            return e.Message;
        }
    }
}
