using System.Globalization;
using System.Text;

namespace ClipEngine.Desktop.Services;

// One rule from detection.ini, as Detection Studio edits it. See src/detect/DetectionRules.h.
internal sealed class StudioRule
{
    public string Game { get; set; } = "";
    public string Event { get; set; } = "kill";
    public string Suffix { get; set; } = "";  // the "<name>" in [game.event.<name>], for more than one rule per event
    public double Left { get; set; } = 0.35;
    public double Top { get; set; } = 0.4;
    public double Width { get; set; } = 0.3;
    public double Height { get; set; } = 0.15;
    public string Words { get; set; } = "";
    public string Exclude { get; set; } = "";
    public int CooldownMs { get; set; } = 1000;
    public int Threshold { get; set; }

    public string Section => Suffix.Length > 0 ? $"{Game}.{Event}.{Suffix}" : $"{Game}.{Event}";
    public string RegionText => string.Join(", ", new[] { Left, Top, Width, Height }.Select(Number));

    internal static string Number(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

// A game added in detection.ini as [game.<id>].
internal sealed record StudioGame(string Id, string Name, IReadOnlyList<string> Executables);

// Reads detection.ini and writes back one game's rules, leaving everything else (other games,
// comments, [detection]) as it was.
internal sealed class DetectionIni
{
    private readonly List<string> _lines;

    private DetectionIni(IEnumerable<string> lines) => _lines = lines.ToList();

    public static DetectionIni Parse(string text) => new(text.Replace("\r\n", "\n").Split('\n'));

    // The user's copy wins over the one next to the app, like the engine reads them (src/main.cpp).
    public static string UserFile => Path.Combine(AppPaths.DataDir, "detection.ini");
    public static string ShippedFile => Path.Combine(AppContext.BaseDirectory, "detection.ini");

    public static DetectionIni Load()
    {
        foreach (var file in new[] { UserFile, ShippedFile })
        {
            try
            {
                if (File.Exists(file)) return Parse(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Parse("[detection]\nsample_ms = 250\n");
    }

    public void SaveToUserFile()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(UserFile, ToString());
    }

    public override string ToString() => string.Join("\n", _lines).TrimEnd('\n') + "\n";

    public IReadOnlyList<StudioRule> Rules
    {
        get
        {
            var rules = new List<StudioRule>();
            foreach (var (name, body) in Sections())
            {
                var parts = name.Split('.');
                if (parts.Length < 2 || parts[0] == "game" || !EngineSettings.AllEvents.Contains(parts[1])) continue;
                var rule = new StudioRule { Game = parts[0], Event = parts[1], Suffix = string.Join('.', parts.Skip(2)) };
                foreach (var (key, value) in body)
                {
                    switch (key)
                    {
                        case "region":
                            var v = value.Split(',').Select(p => double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                            if (v.Length == 4 && v.All(double.IsFinite)) (rule.Left, rule.Top, rule.Width, rule.Height) = (v[0], v[1], v[2], v[3]);
                            break;
                        case "words": rule.Words = value; break;
                        case "exclude": rule.Exclude = value; break;
                        case "cooldown_ms": if (int.TryParse(value, CultureInfo.InvariantCulture, out var ms)) rule.CooldownMs = ms; break;
                        case "threshold": if (int.TryParse(value, CultureInfo.InvariantCulture, out var t)) rule.Threshold = t; break;
                    }
                }
                rules.Add(rule);
            }
            return rules;
        }
    }

    public IReadOnlyList<StudioGame> Games =>
        Sections().Where(s => s.Name.StartsWith("game.", StringComparison.Ordinal) && s.Name.Count(c => c == '.') == 1)
                  .Select(s => new StudioGame(
                      s.Name[5..],
                      s.Body.LastOrDefault(kv => kv.Key == "name").Value ?? s.Name[5..],
                      (s.Body.LastOrDefault(kv => kv.Key == "exe").Value ?? "")
                          .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                  .ToList();

    // Replaces every rule of the game (and its [game.<id>] section, when game is given) with these.
    public void SetGame(string gameId, StudioGame? game, IEnumerable<StudioRule> rules)
    {
        RemoveSections(name => name.StartsWith(gameId + ".", StringComparison.Ordinal) || (game is not null && name == "game." + gameId));

        var block = new StringBuilder();
        block.Append('\n').Append($"# ---------------------------------------------------------------------- {game?.Name ?? gameId}").Append('\n');
        block.Append("# Made in Detection Studio.\n");
        if (game is not null)
        {
            block.Append($"\n[game.{gameId}]\nname = {game.Name}\nexe = {string.Join(" | ", game.Executables)}\n");
        }
        foreach (var rule in rules)
        {
            block.Append($"\n[{rule.Section}]\nregion = {rule.RegionText}\nwords = {rule.Words.Trim()}\n");
            if (rule.Exclude.Trim().Length > 0) block.Append($"exclude = {rule.Exclude.Trim()}\n");
            if (rule.CooldownMs != 1000) block.Append($"cooldown_ms = {rule.CooldownMs}\n");
            if (rule.Threshold > 0) block.Append($"threshold = {rule.Threshold}\n");
        }
        while (_lines.Count > 0 && _lines[^1].Trim().Length == 0) _lines.RemoveAt(_lines.Count - 1);
        _lines.AddRange(block.ToString().TrimEnd('\n').Split('\n'));
    }

    // Sections in file order, with their key = value lines (keys lower-cased, comments dropped).
    private IEnumerable<(string Name, List<KeyValuePair<string, string>> Body)> Sections()
    {
        string? name = null;
        var body = new List<KeyValuePair<string, string>>();
        foreach (var raw in _lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (name is not null) yield return (name, body);
                name = line[1..^1].Trim().ToLowerInvariant();
                body = [];
                continue;
            }
            var eq = line.IndexOf('=');
            if (name is not null && eq > 0) body.Add(new(line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim()));
        }
        if (name is not null) yield return (name, body);
    }

    // Removes matching sections, with the blank lines above them
    private void RemoveSections(Func<string, bool> match)
    {
        var i = 0;
        while (i < _lines.Count)
        {
            var line = StripComment(_lines[i]).Trim();
            if (!(line.StartsWith('[') && line.EndsWith(']') && match(line[1..^1].Trim().ToLowerInvariant())))
            {
                i++;
                continue;
            }
            var end = i + 1;
            while (end < _lines.Count && !StripComment(_lines[end]).Trim().StartsWith('[')) end++;
            // Keep comments that introduce the next section; they belong to it.
            if (end < _lines.Count)
            {
                while (end > i + 1 && (_lines[end - 1].Trim().Length == 0 || _lines[end - 1].TrimStart().StartsWith('#'))) end--;
            }
            var start = i;
            // and the "# ---- Game" banner and note above a game's first section.
            while (start > 0 && (_lines[start - 1].Trim().Length == 0 || _lines[start - 1].TrimStart().StartsWith("# ---")
                                 || _lines[start - 1].TrimStart().StartsWith("# Made in Detection Studio"))) start--;
            _lines.RemoveRange(start, end - start);
            i = start;
        }
    }

    private static string StripComment(string line) => line.IndexOf('#') is var hash and >= 0 ? line[..hash] : line;
}
