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
    // Settings Studio doesn't edit (position, sample_ms, image and the like), kept as they were
    // so that saving a game in Studio doesn't lose them. Losing position turned killfeed rules
    // into "anywhere" rules, which counted every line with your name as a kill and a death.
    public List<KeyValuePair<string, string>> Other { get; } = [];

    public string Position => Setting("position") ?? "anywhere";
    public string? Image => Setting("image");
    public bool SkipTag => Flag("skip_tag");
    public bool ConfirmOnly => Flag("confirm_only");
    public bool IsSound => Setting("sound") is not null;
    // Mentions the player's own name, which the engine fills in, and leaves the rule off without one.
    public bool NeedsPlayer => (Words + "|" + Exclude).Contains("{player}", StringComparison.OrdinalIgnoreCase);

    public int? IntSetting(string key) =>
        int.TryParse(Setting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private bool Flag(string key) => Setting(key)?.Trim().ToLowerInvariant() is "true" or "yes" or "1";

    // A copy to change without changing this one.
    public StudioRule Clone()
    {
        var copy = new StudioRule
        {
            Game = Game, Event = Event, Suffix = Suffix, Left = Left, Top = Top, Width = Width, Height = Height,
            Words = Words, Exclude = Exclude, CooldownMs = CooldownMs, Threshold = Threshold,
        };
        copy.Other.AddRange(Other);
        return copy;
    }

    public string? Setting(string key) => Other.LastOrDefault(kv => kv.Key == key).Value;

    // Sets one of the other settings, or with null removes it.
    public void SetSetting(string key, string? value)
    {
        var at = Other.FindIndex(kv => kv.Key == key);
        Other.RemoveAll(kv => kv.Key == key);
        if (value is null) return;
        Other.Insert(at < 0 ? Other.Count : Math.Min(at, Other.Count), new(key, value));
    }

    public string Section => Suffix.Length > 0 ? $"{Game}.{Event}.{Suffix}" : $"{Game}.{Event}";
    public string RegionText => string.Join(", ", new[] { Left, Top, Width, Height }.Select(Number));

    internal static string Number(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

// A game added in detection.ini as [game.<id>].
internal sealed record StudioGame(string Id, string Name, IReadOnlyList<string> Executables);

// Reads detection.ini and writes back one game's rules, leaving everything else (other games,
// comments, [detection]) as it was.
//
// The engine lays the user's copy (UserFile) over the one that ships with the app: a game with
// rules in the user's copy uses those, every other game the shipped ones (MergeDetectionRules in
// src/detect/DetectionRules.cpp). So the user's copy holds only the games they changed.
internal sealed class DetectionIni
{
    // The first line of a user's copy that holds only their changes. Copies without it are
    // whole copies of the shipped file, from before 0.6, and are trimmed by MigrateUserFile.
    internal const string ChangesHeader = "# Your changes to Clip Engine's detection rules.";

    private const string ChangesIntro = ChangesHeader + "\n" +
        "#\n" +
        "# A game with rules here uses them instead of its built-in rules (detection.ini next to the\n" +
        "# app); every other game uses the built-in ones, so fixes in updates still reach it.\n" +
        "# Detection Studio writes this file. To go back to a game's built-in rules, use \"Built-in\n" +
        "# rules\" in Detection Studio, or delete the game's sections here.";

    private readonly List<string> _lines;

    private DetectionIni(IEnumerable<string> lines) => _lines = lines.ToList();

    public static DetectionIni Parse(string text) => new(text.Replace("\r\n", "\n").Split('\n'));

    // The user's copy wins over the one next to the app, like the engine reads them (src/main.cpp).
    public static string UserFile => Path.Combine(AppPaths.DataDir, "detection.ini");
    public static string ShippedFile => Path.Combine(AppContext.BaseDirectory, "detection.ini");

    // An empty user's copy, ready for changes.
    public static DetectionIni NewChanges() => Parse(ChangesIntro + "\n");

    public static DetectionIni LoadShipped() => LoadFile(ShippedFile) ?? Parse("[detection]\nsample_ms = 250\n");

    public static DetectionIni LoadUser() => LoadFile(UserFile) ?? NewChanges();

    private static DetectionIni? LoadFile(string file)
    {
        try
        {
            if (File.Exists(file)) return Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    // Before 0.6 the user's copy was a whole copy of the shipped file, which hid every later fix
    // to the built-in rules. This trims it to the games really changed, keeping a backup of the
    // old file. Returns whether the file was changed.
    public static bool MigrateUserFile()
    {
        if (LoadFile(UserFile) is not { } user || LoadFile(ShippedFile) is not { } shipped) return false;
        if (user.ChangesOnly(shipped) is not { } trimmed) return false;
        try
        {
            File.Copy(UserFile, Path.Combine(AppPaths.DataDir, "detection.before-0.6.ini"), overwrite: true);
            File.WriteAllText(UserFile, trimmed.ToString());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A whole copy of the shipped file trimmed to what the user changed: games made in Detection
    // Studio (which marked them "Made in Detection Studio") and games the shipped file has no
    // rules for, unless they're the same as the shipped ones, and games added with [game.<id>].
    // Every other game was copied along from an older version and goes back to the built-in
    // rules. Null when this is already a copy of changes only.
    internal DetectionIni? ChangesOnly(DetectionIni shipped)
    {
        if (_lines.Count > 0 && _lines[0].Trim() == ChangesHeader) return null;

        var madeInStudio = new HashSet<string>();
        var inStudio = false;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("# ---", StringComparison.Ordinal)) inStudio = false;
            else if (line.StartsWith("# Made in Detection Studio", StringComparison.Ordinal)) inStudio = true;
            else if (inStudio && StripComment(line).Trim() is var header && header.StartsWith('[') && header.EndsWith(']'))
                madeInStudio.Add(GameOf(header[1..^1].Trim().ToLowerInvariant()));
        }

        var shippedGames = shipped.Rules.Select(r => r.Game).ToHashSet();
        var custom = Games.Select(g => g.Id).ToHashSet();
        var keep = Rules.Select(r => r.Game).Distinct()
            .Where(g => custom.Contains(g) || madeInStudio.Contains(g) || !shippedGames.Contains(g))
            .Where(g => custom.Contains(g) || RulesText(g) != shipped.RulesText(g))
            .ToHashSet();

        var result = NewChanges();
        var userSample = Setting("detection", "sample_ms");
        if (userSample is not null && userSample != shipped.Setting("detection", "sample_ms"))
            result._lines.AddRange(["", "[detection]", $"sample_ms = {userSample}"]);
        foreach (var game in keep.Order(StringComparer.Ordinal))
        {
            result._lines.Add("");
            result._lines.Add($"# ---------------------------------------------------------------------- {game}");
            result._lines.Add("# Made in Detection Studio.");
            if (!custom.Contains(game)) result._lines.AddRange(["", $"[{game}.edited]"]);
            foreach (var (name, lines) in RawSections())
            {
                if (name == "game." + game || GameOf(name) == game) result._lines.AddRange(["", .. lines]);
            }
        }
        // Games added by hand with no rules yet.
        foreach (var (name, lines) in RawSections())
        {
            if (name.StartsWith("game.", StringComparison.Ordinal) && !keep.Contains(name[5..])) result._lines.AddRange(["", .. lines]);
        }
        return result;
    }

    // Games this copy gives its own rules for, which the engine uses instead of the shipped ones.
    public IReadOnlySet<string> EditedGames =>
        Sections().Select(s => s.Name.Split('.'))
                  .Where(p => p.Length >= 2 && p[0] != "game" && (p[1] == "edited" || EngineSettings.AllEvents.Contains(p[1])))
                  .Select(p => p[0]).ToHashSet();

    public IReadOnlyList<StudioRule> RulesFor(string gameId) => Rules.Where(r => r.Game == gameId).ToList();

    // The game's rules as they'd be written, for telling whether two copies say the same.
    internal string RulesText(string gameId)
    {
        var text = new StringBuilder();
        foreach (var rule in RulesFor(gameId).OrderBy(r => r.Section, StringComparer.Ordinal)) WriteRule(text, rule);
        return text.ToString();
    }

    internal static string RulesText(IEnumerable<StudioRule> rules)
    {
        var text = new StringBuilder();
        foreach (var rule in rules.OrderBy(r => r.Section, StringComparer.Ordinal)) WriteRule(text, rule);
        return text.ToString();
    }

    // Takes the game out of this copy, so it goes back to its built-in rules. [game.<id>] stays.
    public void RemoveGame(string gameId) => RemoveSections(name => name.StartsWith(gameId + ".", StringComparison.Ordinal));

    // How often the screen is read, from [detection] sample_ms; null when this copy doesn't say.
    public int? SampleMs => int.TryParse(Setting("detection", "sample_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
        ? ms : null;

    private string? Setting(string section, string key) =>
        Sections().LastOrDefault(s => s.Name == section).Body?.LastOrDefault(kv => kv.Key == key).Value;

    private static string GameOf(string section) => section.StartsWith("game.", StringComparison.Ordinal) ? section[5..] : section.Split('.')[0];

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
                        default: rule.Other.Add(new(key, value)); break;
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
    // Its scoreboard sections aren't rules the Studio edits, so they stay.
    public void SetGame(string gameId, StudioGame? game, IEnumerable<StudioRule> rules)
    {
        RemoveSections(name => (name.StartsWith(gameId + ".", StringComparison.Ordinal)
                                && !name.StartsWith(gameId + ".scoreboard", StringComparison.Ordinal))
                               || (game is not null && name == "game." + gameId));

        var block = new StringBuilder();
        block.Append('\n').Append($"# ---------------------------------------------------------------------- {game?.Name ?? gameId}").Append('\n');
        block.Append("# Made in Detection Studio.\n");
        if (game is not null)
        {
            block.Append($"\n[game.{gameId}]\nname = {game.Name}\nexe = {string.Join(" | ", game.Executables)}\n");
        }
        // Marks the game as changed even with every rule removed, so its built-in rules stay off.
        block.Append($"\n[{gameId}.edited]\n");
        foreach (var rule in rules) WriteRule(block, rule);
        while (_lines.Count > 0 && _lines[^1].Trim().Length == 0) _lines.RemoveAt(_lines.Count - 1);
        _lines.AddRange(block.ToString().TrimEnd('\n').Split('\n'));
    }

    private static void WriteRule(StringBuilder block, StudioRule rule)
    {
        block.Append($"\n[{rule.Section}]\nregion = {rule.RegionText}\n");
        if (rule.Words.Trim().Length > 0 || (rule.Image is null && rule.Setting("sound") is null))
            block.Append($"words = {rule.Words.Trim()}\n");
        if (rule.Exclude.Trim().Length > 0) block.Append($"exclude = {rule.Exclude.Trim()}\n");
        if (rule.CooldownMs != 1000) block.Append($"cooldown_ms = {rule.CooldownMs}\n");
        if (rule.Threshold > 0) block.Append($"threshold = {rule.Threshold}\n");
        foreach (var (key, value) in rule.Other) block.Append($"{key} = {value}\n");
    }

    // Each section's name and its lines as written, up to the next section, without the
    // blank lines and comments that introduce the next one.
    private IEnumerable<(string Name, List<string> Lines)> RawSections()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = StripComment(_lines[i]).Trim();
            if (!(line.StartsWith('[') && line.EndsWith(']'))) continue;
            var end = i + 1;
            while (end < _lines.Count && !StripComment(_lines[end]).Trim().StartsWith('[')) end++;
            while (end > i + 1 && (_lines[end - 1].Trim().Length == 0 || _lines[end - 1].TrimStart().StartsWith('#'))) end--;
            yield return (line[1..^1].Trim().ToLowerInvariant(), _lines.GetRange(i, end - i));
        }
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
