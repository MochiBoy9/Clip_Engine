using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

// Settings that only the desktop app uses.
internal sealed class DesktopSettings
{
    public bool CloseToTray { get; set; } = true;

    // The recording resolution picked in Settings, in lines; 0 is the most the plan allows. The engine gets the
    // smaller of this and the plan's (Resolutions.Effective).
    public int Resolution { get; set; }

    // Friends see the squad invite next to your name, so they can join.
    public bool ShareSquadWithFriends { get; set; } = true;

    // The colour theme, from AccentThemes. Only supporters can pick one other than the default.
    public string AccentTheme { get; set; } = "hazard";

    // Ultra: the colour picked on the colour wheel (used while AccentTheme is "wheel"), and adaptive themes, with or
    // without the background moving now and then.
    public string CustomAccent { get; set; } = "#FFC21A";
    public bool AdaptiveTheme { get; set; } = true;
    public bool BackgroundMotion { get; set; } = true;

    // The side panel that docks to the edge of the screen.
    public string SidePanelEdge { get; set; } = "right";
    public string SidePanelHotkey { get; set; } = "Alt+Shift+C";

    // Upload limit the share copy is made to fit: 10 MB for free Discord accounts.
    public int ShareLimitMb { get; set; } = 10;

    // The clip player's volume, 0 to 100.
    public int PlayerVolume { get; set; } = 80;

    // The editor's export quality, from EditExport.Qualities: "Original", "1080p" or "720p".
    public string EditExportQuality { get; set; } = "Original";

    // Storage cleanup: delete clips older than this many days, and the oldest clips once the
    // library is over this many GB. 0 turns a rule off; both are off until the user picks one.
    public int DeleteAfterDays { get; set; }
    public int MaxLibraryGb { get; set; }

    // A small "Clip saved" notice over the game.
    public bool GameNotifications { get; set; } = true;

    // And one for each moment as it happens: kills, multi-kills, revives, a new personal best.
    public bool MomentNotices { get; set; } = true;

    // And one when a friend comes online or starts playing something.
    public bool FriendNotices { get; set; } = true;

    // The FPS and hardware readout over the game.
    public StatsOverlaySettings StatsOverlay { get; set; } = new();

    // Clips page: show matches instead of single clips.
    public bool ShowMatches { get; set; }

    // Games page: list every supported game, not just the installed ones.
    public bool ShowAllGames { get; set; }

    // Game pictures missing from this PC are fetched from the Steam store.
    public bool GameArtOnline { get; set; } = true;

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

    // Inviting friends: this PC's invite code, and whether it was a new install for a friend's invite.
    public ReferralSettings Referral { get; set; } = new();

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

// The stats overlay: whether it's up, which corner it sits in, its hotkey and what it shows.
internal sealed class StatsOverlaySettings
{
    public bool On { get; set; }
    public string Corner { get; set; } = "top-left";
    public string Hotkey { get; set; } = "Alt+Shift+F";
    public bool Fps { get; set; } = true;
    public bool Lows { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool Vram { get; set; }
    public bool Ram { get; set; } = true;

    [JsonIgnore]
    public Stats.StatsChoice Choice => new(Fps, Lows, Cpu, Gpu, GpuTemp, Vram, Ram);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class DesktopSettingsJson : JsonSerializerContext;
