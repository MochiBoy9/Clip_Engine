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
    public string Codec { get; set; } = "auto";
    public bool GameAudio { get; set; } = true;
    public bool MicAudio { get; set; } = true;
    public string SaveHotkey { get; set; } = "F8";
    public string RecordHotkey { get; set; } = "F9";
    public bool RecordWithoutGame { get; set; }
    public bool Detection { get; set; } = true;
    public string ReadingQuality { get; set; } = "medium";
    public bool AutoClip { get; set; } = true;
    public List<string> AutoClipEvents { get; set; } = [.. AllEvents];
    public int ClipBeforeSeconds { get; set; } = 10;
    public int ClipAfterSeconds { get; set; } = 5;

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
                case "codec": settings.Codec = value.ToLowerInvariant(); break;
                case "game_audio": settings.GameAudio = ParseBool(value, settings.GameAudio); break;
                case "mic_audio": settings.MicAudio = ParseBool(value, settings.MicAudio); break;
                case "save_hotkey": settings.SaveHotkey = value; break;
                case "record_hotkey": settings.RecordHotkey = value; break;
                case "record_without_game": settings.RecordWithoutGame = ParseBool(value, settings.RecordWithoutGame); break;
                case "detection": settings.Detection = ParseBool(value, settings.Detection); break;
                case "reading_quality":
                    if (ReadingQualities.Contains(value.ToLowerInvariant())) settings.ReadingQuality = value.ToLowerInvariant();
                    break;
                case "auto_clip": settings.AutoClip = ParseBool(value, settings.AutoClip); break;
                case "auto_clip_events":
                    settings.AutoClipEvents = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                   .Select(e => e.ToLowerInvariant()).Where(AllEvents.Contains).ToList();
                    break;
                case "clip_before_seconds": settings.ClipBeforeSeconds = ParseInt(value, settings.ClipBeforeSeconds); break;
                case "clip_after_seconds": settings.ClipAfterSeconds = ParseInt(value, settings.ClipAfterSeconds); break;
            }
        }
        return settings;
    }

    public void Save(string file)
    {
        var values = new Dictionary<string, string>
        {
            ["output_dir"] = OutputDir,
            ["buffer_seconds"] = Format(BufferSeconds),
            ["clip_seconds"] = Format(ClipSeconds),
            ["fps"] = Format(Fps),
            ["bitrate_mbps"] = Format(BitrateMbps),
            ["codec"] = Codec,
            ["game_audio"] = Format(GameAudio),
            ["mic_audio"] = Format(MicAudio),
            ["save_hotkey"] = SaveHotkey,
            ["record_hotkey"] = RecordHotkey,
            ["record_without_game"] = Format(RecordWithoutGame),
            ["detection"] = Format(Detection),
            ["reading_quality"] = ReadingQuality,
            ["auto_clip"] = Format(AutoClip),
            ["auto_clip_events"] = string.Join(", ", AllEvents.Where(AutoClipEvents.Contains)),
            ["clip_before_seconds"] = Format(ClipBeforeSeconds),
            ["clip_after_seconds"] = Format(ClipAfterSeconds),
        };

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
