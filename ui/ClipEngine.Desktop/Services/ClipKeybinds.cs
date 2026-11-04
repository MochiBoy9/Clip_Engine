using System.Globalization;
using System.Text;

namespace ClipEngine.Desktop.Services;

// A key that saves a clip of its own length.
public sealed record ClipKeybind(string Hotkey, int Seconds);

// The clip keys in clip_engine.ini, as the engine reads them (src/core/Hotkeys.cpp): lists of
// "key seconds" separated by commas. more_clip_hotkeys holds the ones besides the save hotkey,
// and clip_hotkeys.<game id> a game's own set, which the engine uses instead of the save hotkey
// and more_clip_hotkeys while that game is recorded and reads each time the game starts.
// Only those lines are touched here.
internal static class ClipKeybinds
{
    public const string MoreKey = "more_clip_hotkeys";
    public const int MinSeconds = 5;
    // Room for a handful of lengths; more would be hard to remember anyway.
    public const int MaxKeys = 8;

    public static string GameKey(string gameId) => "clip_hotkeys." + gameId.ToLowerInvariant();

    public static List<ClipKeybind> Parse(string value)
    {
        var keys = new List<ClipKeybind>();
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = entry.LastIndexOfAny([' ', '\t']);
            if (space <= 0) continue;
            var hotkey = entry[..space].Trim();
            var number = entry[(space + 1)..].Trim();
            if (number.EndsWith('s') || number.EndsWith('S')) number = number[..^1];
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) continue;
            if (!IsKey(hotkey)) continue;
            keys.Add(new ClipKeybind(hotkey, seconds));
        }
        return keys;
    }

    public static string Format(IEnumerable<ClipKeybind> keys) =>
        string.Join(", ", keys.Select(k => $"{k.Hotkey.Trim()} {k.Seconds.ToString(CultureInfo.InvariantCulture)}"));

    // Whether the engine reads this as a hotkey (ParseHotkey in src/core/Hotkeys.cpp).
    public static bool IsKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var haveKey = false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = raw.ToLowerInvariant();
            if (part.Length == 0) return false;
            if (part is "ctrl" or "control" or "alt" or "shift" or "win") continue;
            if (haveKey) return false;
            haveKey = true;
            var ok = (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                     || (part[0] == 'f' && part.Length is 2 or 3
                         && int.TryParse(part[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
                     || part is "printscreen" or "print" or "insert" or "home" or "end" or "pageup" or "pagedown" or "pause";
            if (!ok) return false;
        }
        return haveKey;
    }

    // What's wrong with a key typed in, or null when it's fine. A key on its own works (F8),
    // but a bare letter or digit would go off while typing in chat.
    public static string? Problem(string? text)
    {
        if (!IsKey(text)) return "Not a key Clip Engine knows. Try F7, Alt+F8 or Ctrl+Shift+S.";
        var parts = text!.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Length == 1) return "Add Ctrl, Alt or Shift to a letter or digit, like Alt+" + parts[0].ToUpperInvariant();
        return null;
    }

    // The same key written two ways ("alt+f8" and "Alt + F8") is the same key.
    public static string Normalize(string hotkey) =>
        string.Join("+", hotkey.Split('+', StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant() is "control" ? "ctrl" : p.ToLowerInvariant())
            .Order(StringComparer.Ordinal));

    // A game's own clip keys, or null when it uses the ones from Settings.
    public static List<ClipKeybind>? LoadForGame(string file, string gameId)
    {
        var value = Read(file, GameKey(gameId));
        if (value is null) return null;
        var keys = Parse(value);
        return keys.Count == 0 ? null : keys;
    }

    // Sets a game's own clip keys; null or none takes the game back to the ones from Settings.
    public static void SaveForGame(string file, string gameId, IReadOnlyList<ClipKeybind>? keys)
    {
        var key = GameKey(gameId);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var index = lines.FindIndex(l => TryValue(l, key, out _));
        if (index < 0 && (keys is null || keys.Count == 0)) return;
        // One line per game, where the first one was.
        lines.RemoveAll(l => TryValue(l, key, out _));
        if (keys is { Count: > 0 }) lines.Insert(index < 0 ? lines.Count : index, $"{key} = {Format(keys)}");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }

    // The last value given for this setting, as the engine takes the last one.
    private static string? Read(string file, string key)
    {
        if (!File.Exists(file)) return null;
        string? found = null;
        foreach (var line in File.ReadAllLines(file))
        {
            if (TryValue(line, key, out var value)) found = value;
        }
        return found;
    }

    private static bool TryValue(string line, string key, out string value)
    {
        value = "";
        var hash = line.IndexOf('#');
        var text = hash >= 0 ? line[..hash] : line;
        var eq = text.IndexOf('=');
        if (eq < 0 || !text[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return false;
        value = text[(eq + 1)..].Trim();
        return true;
    }
}
