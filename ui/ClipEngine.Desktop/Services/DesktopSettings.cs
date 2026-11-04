using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

// Settings that only the desktop app uses.
internal sealed class DesktopSettings
{
    public bool CloseToTray { get; set; } = true;

    // The colour theme, from AccentThemes. Only supporters can pick one other than the default.
    public string AccentTheme { get; set; } = "hazard";

    // The side panel that docks to the edge of the screen.
    public string SidePanelEdge { get; set; } = "right";
    public string SidePanelHotkey { get; set; } = "Alt+Shift+C";

    // Upload limit the share copy is made to fit: 10 MB for free Discord accounts.
    public int ShareLimitMb { get; set; } = 10;

    // The clip player's volume, 0 to 100.
    public int PlayerVolume { get; set; } = 80;

    // Storage cleanup: delete clips older than this many days, and the oldest clips once the
    // library is over this many GB. 0 turns a rule off; both are off until the user picks one.
    public int DeleteAfterDays { get; set; }
    public int MaxLibraryGb { get; set; }

    // A small "Clip saved" notice over the game.
    public bool GameNotifications { get; set; } = true;

    // And one for each moment as it happens: kills, multi-kills, revives, a new personal best.
    public bool MomentNotices { get; set; } = true;

    // Clips page: show matches instead of single clips.
    public bool ShowMatches { get; set; }

    // Updates: looked for when the app starts, on GitHub Releases of UpdateRepo (empty for the one the app was built
    // from). UpdateToken is a GitHub token for a private repository, encrypted for this Windows account.
    public bool CheckForUpdates { get; set; } = true;
    public string UpdateRepo { get; set; } = "";
    public string UpdateToken { get; set; } = "";

    // Folders clips shared with you were imported into, shown in the library too.
    public List<string> ImportFolders { get; set; } = [];

    // Clip Engine's online service (server/), for squads and the cloud library. Empty uses the
    // one this build was made with (dotnet publish -p:OnlineServer=https://...).
    public string OnlineServer { get; set; } = "";

    // This PC's id with the online service, so joining a squad again from it keeps your place.
    public string DeviceId { get; set; } = "";

    public Squad.SquadSettings Squad { get; set; } = new();
    public Cloud.CloudSettings Cloud { get; set; } = new();

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
