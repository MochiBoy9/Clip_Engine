using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

// Settings that only the desktop app uses.
internal sealed class DesktopSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool AnimatedBackground { get; set; } = true;

    // The colour theme, from AccentThemes. Only supporters can pick one other than the default.
    public string AccentTheme { get; set; } = "aurora";

    // The side panel that docks to the edge of the screen.
    public string SidePanelEdge { get; set; } = "right";
    public string SidePanelHotkey { get; set; } = "Alt+Shift+C";

    // Upload limit the share copy is made to fit: 10 MB for free Discord accounts.
    public int ShareLimitMb { get; set; } = 10;

    // The clip player's volume, 0 to 100.
    public int PlayerVolume { get; set; } = 80;

    // Folders clips shared with you were imported into, shown in the library too.
    public List<string> ImportFolders { get; set; } = [];

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.DesktopSettingsFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.DesktopSettingsFile),
                                                  DesktopSettingsJson.Default.DesktopSettings) ?? new();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.DesktopSettingsFile, JsonSerializer.Serialize(this, DesktopSettingsJson.Default.DesktopSettings));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class DesktopSettingsJson : JsonSerializerContext;
