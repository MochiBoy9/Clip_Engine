using System.Text;

namespace ClipEngine.Desktop.Services;

// The player_names line of clip_engine.ini: the player's in-game names, which the engine needs
// to tell their kills and deaths apart in a killfeed. The engine reads it again each time a game
// starts, so a change needs no restart. Only that line is touched; EngineSettings leaves it be.
internal static class PlayerNames
{
    private const string Key = "player_names";

    public static IReadOnlyList<string> Load(string file)
    {
        if (!File.Exists(file)) return [];
        foreach (var line in File.ReadAllLines(file))
        {
            if (TryValue(line, out var value)) return Split(value);
        }
        return [];
    }

    public static void Save(string file, IEnumerable<string> names)
    {
        var value = string.Join(", ", names.Select(n => n.Trim()).Where(n => n.Length > 0));
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var index = lines.FindIndex(l => TryValue(l, out _));
        if (index >= 0) lines[index] = $"{Key} = {value}";
        else lines.Add($"{Key} = {value}");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }

    // Names are separated by commas. A '#' starts a comment in the file, so a Riot ID's #tag can't be part of one.
    public static IReadOnlyList<string> Split(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Split('#')[0].Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
