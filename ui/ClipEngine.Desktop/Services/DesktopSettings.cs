using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

// Settings that only the desktop app uses.
internal sealed class DesktopSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool AnimatedBackground { get; set; } = true;

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
