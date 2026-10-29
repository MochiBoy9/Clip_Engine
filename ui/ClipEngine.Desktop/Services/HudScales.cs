using System.Globalization;
using System.Text;

namespace ClipEngine.Desktop.Services;

// The hud_scale line of clip_engine.ini: how big the player has set each game's HUD, in percent,
// for the games where it isn't 100 ("overwatch 90, fortnite 120"). The engine reads it each
// time a game starts (ReadHudScale in src/core/Config.cpp) and places the game's rules to match.
// Only that line is touched; EngineSettings leaves it be.
internal static class HudScales
{
    private const string Key = "hud_scale";
    public const int Min = 50;
    public const int Max = 200;

    public static IReadOnlyDictionary<string, int> Load(string file)
    {
        if (!File.Exists(file)) return new Dictionary<string, int>();
        foreach (var line in File.ReadAllLines(file))
        {
            if (TryValue(line, out var value)) return Parse(value);
        }
        return new Dictionary<string, int>();
    }

    public static int For(string file, string gameId) => Load(file).TryGetValue(gameId, out var percent) ? percent : 100;

    // Sets one game's size; 100 takes it off the line.
    public static void Save(string file, string gameId, int percent)
    {
        var scales = Load(file).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (percent == 100) scales.Remove(gameId);
        else scales[gameId] = Math.Clamp(percent, Min, Max);
        var value = Format(scales);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var index = lines.FindIndex(l => TryValue(l, out _));
        if (index >= 0) lines[index] = $"{Key} = {value}";
        else lines.Add($"{Key} = {value}");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static Dictionary<string, int> Parse(string value)
    {
        var scales = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = entry.LastIndexOfAny([' ', '\t', ':']);
            if (split <= 0) continue;
            var game = entry[..split].Trim().ToLowerInvariant();
            var number = entry[(split + 1)..].Trim().TrimEnd('%');
            if (game.Length > 0 && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                scales[game] = Math.Clamp(percent, 25, 400);
        }
        return scales;
    }

    internal static string Format(IReadOnlyDictionary<string, int> scales) =>
        string.Join(", ", scales.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));

    private static bool TryValue(string line, out string value)
    {
        value = "";
        var hash = line.IndexOf('#');
        var text = hash >= 0 ? line[..hash] : line;
        var eq = text.IndexOf('=');
        if (eq < 0 || !text[..eq].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase)) return false;
        value = text[(eq + 1)..].Trim();
        return true;
    }
}
