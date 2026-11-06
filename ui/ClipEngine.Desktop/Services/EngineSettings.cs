using System.Globalization;
using System.Text;

namespace ClipEngine.Desktop.Services;

// The engine's clip_engine.ini. Saving changes only the value of each line, so comments and layout survive.
internal sealed class EngineSettings
{
    public string OutputDir { get; set; } = "";
    public int BufferSeconds { get; set; } = 60;
    public int ClipSeconds { get; set; } = 30;
    public int Fps { get; set; }
    public int BitrateMbps { get; set; }
    public int Resolution { get; set; }  // the most lines to record, like 1440; 0 = the monitor's own
    public string Codec { get; set; } = "auto";
    public bool GameAudio { get; set; } = true;
    public bool MicAudio { get; set; } = true;
    public bool DiscordAudio { get; set; } = true;
    public bool AppsAudio { get; set; } = true;
    // Each track's volume in percent (0 to 200), and the tracks that are muted, by name (AudioTracks). The engine takes
    // changes to these at once, so the app saves them on their own as they change (SaveAudioLevels).
    public Dictionary<string, int> Volumes { get; set; } = AudioTracks.ToDictionary(t => t, _ => 100);
    public List<string> MutedAudio { get; set; } = [];
    // Saying one of the phrases saves a clip, of its own length or VoiceClipSeconds (0 = ClipSeconds).
    public bool VoiceClips { get; set; }
    public string VoicePhrases { get; set; } = "clip that";
    public int VoiceClipSeconds { get; set; }
    public string VoiceSensitivity { get; set; } = "medium";
    public string SaveHotkey { get; set; } = "F8";
    // Keys besides the save hotkey that each save a clip of their own length.
    public List<ClipKeybind> MoreClipHotkeys { get; set; } = [];
    public string RecordHotkey { get; set; } = "F9";
    public bool RecordWithoutGame { get; set; }
    public bool GameFocus { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    public bool Detection { get; set; } = true;
    public string ReadingQuality { get; set; } = "medium";
    public bool AutoClip { get; set; } = true;
    // Keep the journal of each run in games built around runs (src/detect/Journal.h).
    public bool Journal { get; set; } = true;
    public List<string> AutoClipEvents { get; set; } = [.. AllEvents];
    public int ClipBeforeSeconds { get; set; } = 10;
    public int ClipAfterSeconds { get; set; } = 5;
    public bool ClipLoneDeaths { get; set; } = true;
    public bool AutoHighlights { get; set; } = true;

    // The engine's audio tracks (AudioTrack in src/audio/AudioRouting.h).
    public static readonly IReadOnlyList<string> AudioTracks = ["game", "mic", "discord", "apps"];
    public const int MaxVolume = 200;

    public static readonly IReadOnlyList<string> VoiceSensitivities = ["low", "medium", "high"];

    public int VolumeOf(string track) => Volumes.TryGetValue(track, out var v) ? v : 100;
    public bool IsMuted(string track) => MutedAudio.Contains(track);

    // How hard the engine works at reading the screen (ReadQuality in src/detect/OcrImage.h).
    public static readonly IReadOnlyList<string> ReadingQualities = ["low", "medium", "high", "ultra"];

    // The engine's event names (src/detect/GameEvent.h).
    public static readonly IReadOnlyList<string> AllEvents =
        ["kill", "assist", "death", "revive", "match_start", "match_end", "highlight"];

    public static EngineSettings Load(string file)
    {
        var settings = new EngineSettings();
        if (!File.Exists(file)) return settings;

        foreach (var raw in File.ReadAllLines(file))
        {
            if (!TrySplit(raw, out var key, out var value)) continue;
            switch (key)
            {
                case "output_dir": settings.OutputDir = value; break;
                case "buffer_seconds": settings.BufferSeconds = ParseInt(value, settings.BufferSeconds); break;
                case "clip_seconds": settings.ClipSeconds = ParseInt(value, settings.ClipSeconds); break;
                case "fps": settings.Fps = ParseInt(value, settings.Fps); break;
                case "bitrate_mbps": settings.BitrateMbps = ParseInt(value, settings.BitrateMbps); break;
                case "resolution":
                    var lines = value.ToLowerInvariant().TrimEnd('p');
                    settings.Resolution = lines is "native" or "auto" or "" ? 0 : ParseInt(lines, settings.Resolution);
                    break;
                case "codec": settings.Codec = value.ToLowerInvariant(); break;
                case "game_audio": settings.GameAudio = ParseBool(value, settings.GameAudio); break;
                case "mic_audio": settings.MicAudio = ParseBool(value, settings.MicAudio); break;
                case "discord_audio": settings.DiscordAudio = ParseBool(value, settings.DiscordAudio); break;
                case "apps_audio": settings.AppsAudio = ParseBool(value, settings.AppsAudio); break;
                case "muted_audio":
                    settings.MutedAudio = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                               .Select(t => t.ToLowerInvariant()).Where(AudioTracks.Contains).Distinct().ToList();
                    break;
                case var volume when volume.EndsWith("_volume", StringComparison.Ordinal) && AudioTracks.Contains(volume[..^7]):
                    settings.Volumes[volume[..^7]] = Math.Clamp(ParseInt(value.TrimEnd('%'), 100), 0, MaxVolume);
                    break;
                case "voice_clips": settings.VoiceClips = ParseBool(value, settings.VoiceClips); break;
                case "voice_phrases": settings.VoicePhrases = value; break;
                case "voice_clip_seconds": settings.VoiceClipSeconds = ParseInt(value, settings.VoiceClipSeconds); break;
                case "voice_sensitivity":
                    if (VoiceSensitivities.Contains(value.ToLowerInvariant())) settings.VoiceSensitivity = value.ToLowerInvariant();
                    break;
                case "save_hotkey": settings.SaveHotkey = value; break;
                case ClipKeybinds.MoreKey: settings.MoreClipHotkeys = ClipKeybinds.Parse(value); break;
                case "record_hotkey": settings.RecordHotkey = value; break;
                case "record_without_game": settings.RecordWithoutGame = ParseBool(value, settings.RecordWithoutGame); break;
                case "game_focus": settings.GameFocus = ParseBool(value, settings.GameFocus); break;
                case "discord_presence": settings.DiscordPresence = ParseBool(value, settings.DiscordPresence); break;
                case "detection": settings.Detection = ParseBool(value, settings.Detection); break;
                case "reading_quality":
                    if (ReadingQualities.Contains(value.ToLowerInvariant())) settings.ReadingQuality = value.ToLowerInvariant();
                    break;
                case "auto_clip": settings.AutoClip = ParseBool(value, settings.AutoClip); break;
                case "journal": settings.Journal = ParseBool(value, settings.Journal); break;
                case "auto_clip_events":
                    settings.AutoClipEvents = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                   .Select(e => e.ToLowerInvariant()).Where(AllEvents.Contains).ToList();
                    break;
                case "clip_before_seconds": settings.ClipBeforeSeconds = ParseInt(value, settings.ClipBeforeSeconds); break;
                case "clip_after_seconds": settings.ClipAfterSeconds = ParseInt(value, settings.ClipAfterSeconds); break;
                case "clip_lone_deaths": settings.ClipLoneDeaths = ParseBool(value, settings.ClipLoneDeaths); break;
                case "auto_highlights": settings.AutoHighlights = ParseBool(value, settings.AutoHighlights); break;
            }
        }
        return settings;
    }

    public void Save(string file)
    {
        var values = new Dictionary<string, string>(AudioLevelValues())
        {
            ["output_dir"] = OutputDir,
            ["buffer_seconds"] = Format(BufferSeconds),
            ["clip_seconds"] = Format(ClipSeconds),
            ["fps"] = Format(Fps),
            ["bitrate_mbps"] = Format(BitrateMbps),
            ["resolution"] = Resolution > 0 ? Format(Resolution) : "native",
            ["codec"] = Codec,
            ["game_audio"] = Format(GameAudio),
            ["mic_audio"] = Format(MicAudio),
            ["discord_audio"] = Format(DiscordAudio),
            ["apps_audio"] = Format(AppsAudio),
            ["voice_clips"] = Format(VoiceClips),
            ["voice_phrases"] = VoicePhrases.Replace('#', ' ').Trim(),
            ["voice_clip_seconds"] = Format(VoiceClipSeconds),
            ["voice_sensitivity"] = VoiceSensitivity,
            ["save_hotkey"] = SaveHotkey,
            [ClipKeybinds.MoreKey] = ClipKeybinds.Format(MoreClipHotkeys),
            ["record_hotkey"] = RecordHotkey,
            ["record_without_game"] = Format(RecordWithoutGame),
            ["game_focus"] = Format(GameFocus),
            ["discord_presence"] = Format(DiscordPresence),
            ["detection"] = Format(Detection),
            ["reading_quality"] = ReadingQuality,
            ["auto_clip"] = Format(AutoClip),
            ["journal"] = Format(Journal),
            ["auto_clip_events"] = string.Join(", ", AllEvents.Where(AutoClipEvents.Contains)),
            ["clip_before_seconds"] = Format(ClipBeforeSeconds),
            ["clip_after_seconds"] = Format(ClipAfterSeconds),
            ["clip_lone_deaths"] = Format(ClipLoneDeaths),
            ["auto_highlights"] = Format(AutoHighlights),
        };
        Write(file, values);
    }

    // Saves only the volumes and mutes, leaving every other line as it is in the file, for changes the engine takes
    // while it records.
    public void SaveAudioLevels(string file) => Write(file, AudioLevelValues());

    private Dictionary<string, string> AudioLevelValues()
    {
        var values = AudioTracks.ToDictionary(t => $"{t}_volume", t => Format(Math.Clamp(VolumeOf(t), 0, MaxVolume)));
        values["muted_audio"] = string.Join(", ", AudioTracks.Where(MutedAudio.Contains));
        return values;
    }

    private static void Write(string file, Dictionary<string, string> values)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var written = new HashSet<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TrySplit(lines[i], out var key, out _) || !values.TryGetValue(key, out var value)) continue;
            var eq = lines[i].IndexOf('=');
            lines[i] = $"{lines[i][..eq].TrimEnd()} = {value}";
            written.Add(key);
        }
        foreach (var (key, value) in values)
        {
            if (!written.Contains(key)) lines.Add($"{key} = {value}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }

    // Splits "key = value # comment" the way the engine does: the comment is dropped and the key is lower-cased.
    private static bool TrySplit(string line, out string key, out string value)
    {
        var hash = line.IndexOf('#');
        if (hash >= 0) line = line[..hash];
        var eq = line.IndexOf('=');
        if (eq < 0)
        {
            key = value = "";
            return false;
        }
        key = line[..eq].Trim().ToLowerInvariant();
        value = line[(eq + 1)..].Trim();
        return key.Length > 0;
    }

    private static int ParseInt(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ParseBool(string s, bool fallback) => s.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => fallback,
    };

    private static string Format(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Format(bool v) => v ? "true" : "false";
}
