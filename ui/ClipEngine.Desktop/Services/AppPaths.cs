namespace ClipEngine.Desktop.Services;

// The same locations clip_engine.exe uses (see src/main.cpp and src/core/Config.cpp).
internal static class AppPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip_Engine");

    public static string EngineSettingsFile => Path.Combine(DataDir, "clip_engine.ini");
    public static string EngineLogFile => Path.Combine(DataDir, "clip_engine.log");
    public static string DesktopSettingsFile => Path.Combine(DataDir, "desktop.json");

    public static string DefaultClipsDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clip_Engine");

    public static string EngineExe => Path.Combine(AppContext.BaseDirectory, "clip_engine.exe");
}
