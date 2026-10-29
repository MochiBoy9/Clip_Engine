using Avalonia;

namespace ClipEngine.Desktop;

internal static class Program
{
    // Passed by the copy that installed an update to the one it starts, with its process id.
    internal const string AfterUpdateArg = "--after-update";

    internal static bool StartedAfterUpdate { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // An update starts the new copy before the old one has quite exited; wait for it, so this one is the first.
        if (args is [AfterUpdateArg, var pid, ..])
        {
            StartedAfterUpdate = true;
            args = args[2..];
            WaitForExit(pid);
        }

        // One window at a time. A second launch hands its clip, if it was opened with one, to the
        // running copy and exits.
        using var single = new Mutex(true, @"Local\ClipEngine.Desktop", out var first);
        if (!first)
        {
            ClipEngine.Desktop.Services.InstanceChannel.TrySend(args.FirstOrDefault() ?? "");
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WaitForExit(string pid)
    {
        if (!int.TryParse(pid, out var id)) return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
