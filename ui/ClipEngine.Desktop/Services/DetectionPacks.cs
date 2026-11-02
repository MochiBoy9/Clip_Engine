using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClipEngine.Desktop.Services;

internal sealed class PackException(string message) : Exception(message);

// pack.json in a detection pack.
internal sealed class PackManifest
{
    [JsonPropertyName("format")] public int Format { get; set; } = DetectionPacks.FormatVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("executables")] public List<string> Executables { get; set; } = [];
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
}

// A game's detection, ready to share: its rules from detection.ini, the icons and sounds they
// look and listen for, and what the universal HUD reader has learned about it.
internal sealed record DetectionPack(
    PackManifest Manifest,
    string Rules,                                 // detection.ini sections, file names as in Files
    IReadOnlyDictionary<string, byte[]> Files,    // icons and sounds, by file name
    IReadOnlyList<string> Examples,               // hud_learned.txt lines for the game
    IReadOnlyList<string> Warnings)
{
    public int RuleCount => DetectionPacks.SectionNames(Rules).Count(s => !s.StartsWith("game.", StringComparison.Ordinal)
                                                                         && !s.Contains(".scoreboard", StringComparison.Ordinal));

    public string Summary
    {
        get
        {
            var parts = new List<string> { RuleCount == 1 ? "1 rule" : $"{RuleCount} rules" };
            if (Files.Count > 0) parts.Add(Files.Count == 1 ? "1 icon or sound" : $"{Files.Count} icons and sounds");
            if (Examples.Count > 0) parts.Add(Examples.Count == 1 ? "1 learned line" : $"{Examples.Count} learned lines");
            return string.Join(", ", parts);
        }
    }
}

// Detection packs (.cepack): one file that carries everything Clip Engine knows about reading a
// game, so a game someone has set up works for everyone they share it with from the first match.
// A zip of pack.json, detection.ini (the game's sections only), hud_learned.txt and files/.
// Importing one checks every part of it: it can only change the game it's for, only carry
// pictures and sounds, and its rules can only name files inside it.
internal static partial class DetectionPacks
{
    public const string Extension = ".cepack";
    public const int FormatVersion = 1;
    public const string FolderName = "packs";

    private const long MaxFileBytes = 16L * 1024 * 1024;
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private const int MaxEntries = 256;
    private static readonly string[] FileExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".wav"];
    private static readonly string[] Labels =
        ["none", "menu", "kill", "assist", "death", "revive", "match_start", "match_end", "highlight"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    public static bool IsPackFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    // --- Export ---

    // The pack for a game: the user's rules for it where they have their own, the built-in ones
    // otherwise, with every icon and sound they name, and what the universal reader has learned.
    // findFile turns a file name a rule gives into the file on disk (StudioTools.FindFile).
    public static DetectionPack Build(string gameId, string gameName, IReadOnlyList<string> executables, DetectionIni user,
                                      DetectionIni shipped, string? learnedFile, Func<string, string> findFile,
                                      string author = "", string description = "")
    {
        var warnings = new List<string>();
        var source = user.EditedGames.Contains(gameId) ? user : shipped;
        var text = source.GameSectionsText(gameId);
        // A game added in Detection Studio is described in the user's file even without rules.
        var custom = user.GameSectionsText(gameId, gameSectionOnly: true);
        if (!ReferenceEquals(source, user) && custom.Length > 0) text = custom + "\n" + text;

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var rules = RewriteFiles(text, name =>
        {
            var path = findFile(name);
            if (!File.Exists(path))
            {
                warnings.Add($"{name} wasn't found, so it isn't in the pack");
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var fileName = UniqueName(files, Path.GetFileName(path), bytes);
            files[fileName] = bytes;
            return fileName;
        });

        var examples = new List<string>();
        if (learnedFile is not null && File.Exists(learnedFile))
        {
            foreach (var line in File.ReadAllLines(learnedFile))
            {
                if (ParseExample(line) is { } example && example.Game == gameId) examples.Add(line.TrimEnd('\r'));
            }
        }

        var manifest = new PackManifest
        {
            Id = gameId,
            Name = gameName,
            Executables = executables.ToList(),
            Author = author.Trim(),
            Description = description.Trim(),
            Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            AppVersion = UpdateFeed.CurrentVersion.ToString(3),
        };
        return new DetectionPack(manifest, rules, files, examples, warnings);
    }

    public static void Write(DetectionPack pack, string path)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "pack.json", JsonSerializer.SerializeToUtf8Bytes(pack.Manifest, Json));
            WriteEntry(zip, "detection.ini", Encoding.UTF8.GetBytes(Header(pack.Manifest) + pack.Rules.Trim('\n') + "\n"));
            if (pack.Examples.Count > 0)
                WriteEntry(zip, "hud_learned.txt", Encoding.UTF8.GetBytes(string.Join("\n", pack.Examples) + "\n"));
            foreach (var (name, bytes) in pack.Files) WriteEntry(zip, "files/" + name, bytes);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static string Header(PackManifest m) =>
        $"# Detection pack for {m.Name}" + (m.Author.Length > 0 ? $" by {m.Author}" : "") + ", made with Clip Engine.\n";

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(bytes);
    }

    // --- Import ---

    // Reads and checks a pack. Throws PackException when it isn't one, or is unsafe to install.
    public static DetectionPack Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (InvalidDataException)
        {
            throw new PackException("it isn't a detection pack (not a zip file)");
        }
    }

    public static DetectionPack Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries) throw new PackException("it has too many files in it");
        long total = 0;
        byte[]? manifestBytes = null, rulesBytes = null, examplesBytes = null;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;  // a folder
            if (entry.Length > MaxFileBytes) throw new PackException($"{name} is too big");
            total += entry.Length;
            if (total > MaxTotalBytes) throw new PackException("it's too big");
            switch (name)
            {
                case "pack.json": manifestBytes = ReadAll(entry); continue;
                case "detection.ini": rulesBytes = ReadAll(entry); continue;
                case "hud_learned.txt": examplesBytes = ReadAll(entry); continue;
            }
            var fileName = name.StartsWith("files/", StringComparison.Ordinal) ? name[6..] : null;
            if (fileName is null || !IsSafeFileName(fileName))
            {
                warnings.Add($"{name} was left out: a pack can only carry pictures and sounds");
                continue;
            }
            files[fileName] = ReadAll(entry);
        }
        if (manifestBytes is null || rulesBytes is null) throw new PackException("it's missing pack.json or detection.ini");

        PackManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PackManifest>(manifestBytes) ?? throw new PackException("pack.json is empty");
        }
        catch (JsonException)
        {
            throw new PackException("pack.json can't be read");
        }
        if (manifest.Format > FormatVersion) throw new PackException("it was made by a newer Clip Engine; update to install it");
        manifest.Id = manifest.Id.Trim().ToLowerInvariant();
        if (!IdPattern().IsMatch(manifest.Id)) throw new PackException("its game id isn't valid");
        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;
        manifest.Name = manifest.Name.Trim();

        var rules = OnlyGame(Encoding.UTF8.GetString(rulesBytes), manifest.Id, warnings);
        rules = RewriteFiles(rules, name =>
        {
            var fileName = Path.GetFileName(name.Replace('\\', '/'));
            if (files.ContainsKey(fileName) && IsSafeFileName(fileName)) return fileName;
            warnings.Add($"{name} isn't in the pack, so the rule that uses it was left without it");
            return null;
        });
        if (SectionNames(rules).Count == 0) throw new PackException("it has no rules for its game");

        var examples = new List<string>();
        if (examplesBytes is not null)
        {
            foreach (var line in Encoding.UTF8.GetString(examplesBytes).Split('\n'))
            {
                if (ParseExample(line) is not { } example) continue;
                // Whatever game the line says, it's for the pack's game.
                examples.Add($"{manifest.Id}\t{example.Label}\t{example.Zone}\t{example.Text}");
            }
        }
        // Only files a rule uses are kept.
        var used = UsedFiles(rules);
        foreach (var name in files.Keys.Where(n => !used.Contains(n)).ToList()) files.Remove(name);
        return new DetectionPack(manifest, rules, files, examples, warnings);
    }

    // Installs a pack: its files into packs\<id>\ next to the user's detection.ini, its rules in
    // place of the user's for that game (the old file is kept as detection.before-pack.ini), and
    // what it learned into hud_learned.txt. Returns whether it replaced rules the user had made.
    public static bool Install(DetectionPack pack, DetectionIni user, string dataDir)
    {
        var id = pack.Manifest.Id;
        var replaced = user.EditedGames.Contains(id);
        var folder = Path.Combine(dataDir, FolderName, id);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in pack.Files) File.WriteAllBytes(Path.Combine(folder, name), bytes);

        var rules = RewriteFiles(pack.Rules, name => $"{FolderName}/{id}/{name}");
        var note = $"From the detection pack for {pack.Manifest.Name}" +
                   (pack.Manifest.Author.Length > 0 ? $" by {pack.Manifest.Author}" : "") + ".";
        var userFile = Path.Combine(dataDir, "detection.ini");
        if (File.Exists(userFile)) File.Copy(userFile, Path.Combine(dataDir, "detection.before-pack.ini"), overwrite: true);
        user.ReplaceGameText(id, pack.Manifest.Name, rules, note);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(userFile, user.ToString());

        if (pack.Examples.Count > 0) AppendExamples(Path.Combine(dataDir, "hud_learned.txt"), pack.Examples);
        return replaced;
    }

    // Adds lines to hud_learned.txt that aren't there yet, as the engine does (AppendHudExamples).
    internal static int AppendExamples(string file, IEnumerable<string> lines)
    {
        var known = File.Exists(file)
            ? File.ReadAllLines(file).Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var added = lines.Where(l => known.Add(l)).ToList();
        if (added.Count == 0) return 0;
        var text = new StringBuilder();
        if (!File.Exists(file))
        {
            text.Append("# What Clip Engine's universal HUD reader has learned from your corrections in Check detection.\n");
            text.Append("# One line per example: game (* for any), what it means, the part of the screen, and the text,\n");
            text.Append("# separated by tabs, with {P} for your in-game name. Delete a line to forget it.\n");
        }
        foreach (var line in added) text.Append(line).Append('\n');
        File.AppendAllText(file, text.ToString());
        return added.Count;
    }

    // Forgets what the universal HUD reader learned for one game. Returns how many lines went.
    internal static int ForgetExamples(string file, string gameId)
    {
        if (!File.Exists(file)) return 0;
        var lines = File.ReadAllLines(file);
        var kept = lines.Where(l => ParseExample(l)?.Game != gameId).ToList();
        if (kept.Count == lines.Length) return 0;
        File.WriteAllLines(file, kept);
        return lines.Length - kept.Count;
    }

    // --- Helpers ---

    internal sealed record Example(string Game, string Label, string Zone, string Text);

    // One hud_learned.txt line, or null when it isn't one.
    internal static Example? ParseExample(string line)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0 || line.StartsWith('#') || line.Length > 400) return null;
        var parts = line.Split('\t', 4);
        if (parts.Length != 4) return null;
        var label = parts[1].Trim();
        var text = parts[3].Trim();
        if (!Labels.Contains(label) || text.Length == 0) return null;
        return new Example(parts[0].Trim(), label, parts[2].Trim(), text);
    }

    internal static bool IsSafeFileName(string name) =>
        name.Length is > 0 and <= 120 && name == Path.GetFileName(name) && !name.Contains("..") && !name.StartsWith('.')
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0
        && FileExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    // The section names in detection.ini text, lower-cased.
    internal static List<string> SectionNames(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(StripComment).Select(l => l.Trim())
            .Where(l => l.StartsWith('[') && l.EndsWith(']')).Select(l => l[1..^1].Trim().ToLowerInvariant()).ToList();

    // Only the sections for this game ([game.<id>], [<id>.<event>...], [<id>.scoreboard...]),
    // without [<id>.edited], which the install adds.
    internal static string OnlyGame(string text, string id, List<string> warnings)
    {
        var result = new List<string>();
        var keep = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim().ToLowerInvariant();
                var parts = name.Split('.');
                keep = name == "game." + id
                       || (parts.Length >= 2 && parts[0] == id
                           && (parts[1] == "scoreboard" || EngineSettings.AllEvents.Contains(parts[1])));
                if (!keep && name != id + ".edited") warnings.Add($"[{name}] was left out: a pack can only change the game it's for");
                if (keep) result.Add("");
            }
            if (keep) result.Add(raw.TrimEnd());
        }
        return string.Join("\n", result).Trim('\n') + "\n";
    }

    // Rewrites the file names image and sound settings give, with map; a null from map drops
    // that name (and the setting, when none is left).
    internal static string RewriteFiles(string text, Func<string, string?> map)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = StripComment(lines[i]);
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            if (key is not ("image" or "sound")) continue;
            var names = line[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(map).OfType<string>().ToList();
            if (names.Count == 0)
            {
                lines.RemoveAt(i--);
                continue;
            }
            lines[i] = $"{key} = {string.Join(" | ", names)}";
        }
        return string.Join("\n", lines);
    }

    private static HashSet<string> UsedFiles(string rules)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RewriteFiles(rules, name =>
        {
            used.Add(name);
            return name;
        });
        return used;
    }

    private static string UniqueName(Dictionary<string, byte[]> files, string name, byte[] bytes)
    {
        if (!files.TryGetValue(name, out var existing) || existing.AsSpan().SequenceEqual(bytes)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}_{n}{ext}";
            if (!files.ContainsKey(candidate)) return candidate;
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            // The size a zip claims for a file can be a lie.
            if (output.Length + read > MaxFileBytes) throw new PackException($"{entry.FullName} is too big");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string StripComment(string line) => line.IndexOf('#') is var hash and >= 0 ? line[..hash] : line;
}
