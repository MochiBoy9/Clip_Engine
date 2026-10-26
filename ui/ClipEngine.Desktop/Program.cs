using Avalonia;

namespace ClipEngine.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One window at a time; a second launch just exits.
        using var single = new Mutex(true, @"Local\ClipEngine.Desktop", out var first);
        if (!first) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
