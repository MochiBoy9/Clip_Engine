using Avalonia;

namespace ClipEngine.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
