using System.Globalization;
using System.Text.Json;

namespace ClipEngine.Desktop.Services;

// One recording's timeline, as the engine writes it to <data>\timeline\ (src/app/Timeline.h):
// every moment it detected and every change to the player's totals, clipped or not.
internal sealed class Timeline
{
    public required string Session { get; init; }
    public required string Game { get; init; }
    public List<TimelineMoment> Moments { get; } = [];
    public List<TimelineScores> Scores { get; } = [];

    public DateTime? Start => DateTime.TryParseExact(Session, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                                                     DateTimeStyles.AssumeLocal, out var t) ? t : null;

    public static string Folder => Path.Combine(AppPaths.DataDir, "timeline");

    public static Timeline? Parse(IEnumerable<string> lines)
    {
        Timeline? timeline = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (timeline is null)
                {
                    // The first line names the recording; a file without it isn't a timeline.
                    if (!root.TryGetProperty("session", out var session) || !root.TryGetProperty("game", out var game)) return null;
                    timeline = new Timeline { Session = session.GetString() ?? "", Game = game.GetString() ?? "" };
                    continue;
                }
                var t = root.TryGetProperty("t", out var tv) && tv.TryGetDouble(out var td) ? td : 0;
                var match = root.TryGetProperty("match", out var mv) && mv.TryGetInt32(out var mi) ? mi : 0;
                if (root.TryGetProperty("type", out var type))
                {
                    var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    timeline.Moments.Add(new TimelineMoment(t, match, type.GetString() ?? "", text));
                }
                else if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                {
                    var values = new Dictionary<string, int>();
                    foreach (var p in stats.EnumerateObject())
                        if (p.Value.TryGetInt32(out var v)) values[p.Name] = v;
                    timeline.Scores.Add(new TimelineScores(t, match, values));
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash; the rest of the file still counts.
            }
        }
        return timeline;
    }

    public static IReadOnlyList<Timeline> LoadAll(string folder)
    {
        var timelines = new List<Timeline>();
        if (!Directory.Exists(folder)) return timelines;
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);
                if (Parse(lines) is { } timeline) timelines.Add(timeline);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return timelines;
    }
}

internal sealed record TimelineMoment(double Seconds, int Match, string Type, string Text);
internal sealed record TimelineScores(double Seconds, int Match, IReadOnlyDictionary<string, int> Stats);

// One match as the records see it: the moments counted, or the game's own totals where it showed
// them, whichever is higher (a scoreboard read mid-match can't have the last kills yet).
internal sealed class MatchStats
{
    public required string Game { get; init; }
    public required DateTime Start { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int Revives { get; init; }
    public int Highlights { get; init; }
    // Most kills in a row without dying, and most kills within BurstWindow of each other.
    public int BestStreak { get; init; }
    public int BestBurst { get; init; }
    public string? Result { get; init; }
    // Other numbers the game reported, like score or creep_score.
    public IReadOnlyDictionary<string, int> Extra { get; init; } = new Dictionary<string, int>();
    // Whether the game's own totals were seen, which makes the numbers exact.
    public bool FromScoreboard { get; init; }

    public bool Won => Result is { } r && (r.Contains("VICTORY", StringComparison.OrdinalIgnoreCase) ||
                                           r.Contains("WIN", StringComparison.OrdinalIgnoreCase) ||
                                           r.Contains("EXTRACT", StringComparison.OrdinalIgnoreCase));
    public bool Lost => Result is { } r && (r.Contains("DEFEAT", StringComparison.OrdinalIgnoreCase) ||
                                            r.Contains("LOSE", StringComparison.OrdinalIgnoreCase) ||
                                            r.Contains("LOST", StringComparison.OrdinalIgnoreCase));
    public double KillDeathRatio => Deaths == 0 ? Kills : (double)Kills / Deaths;
    public bool HasCombat => Kills + Deaths + Assists + Revives > 0;
}

// A personal best, like "Most kills in a match: 31 in Valorant on 12 Oct".
internal sealed record PlayerRecord(string Key, string Title, int Value, string ValueText, string Game, DateTime When)
{
    public string Detail => $"{Game} · {When.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
}

internal sealed record GameTotals(string Game, int Matches, int Wins, int Kills, int Deaths, int Assists, int Revives, int BestKills)
{
    public double KillDeathRatio => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

internal sealed class RecordBook
{
    public IReadOnlyList<MatchStats> Matches { get; init; } = [];  // newest first
    public IReadOnlyList<PlayerRecord> Records { get; init; } = [];
    public IReadOnlyList<GameTotals> Games { get; init; } = [];     // most played first

    public int Kills => Matches.Sum(m => m.Kills);
    public int Deaths => Matches.Sum(m => m.Deaths);
    public int Assists => Matches.Sum(m => m.Assists);
    public int Revives => Matches.Sum(m => m.Revives);
    public int Wins => Matches.Count(m => m.Won);
    public double KillDeathRatio => Deaths == 0 ? Kills : (double)Kills / Deaths;

    public PlayerRecord? Find(string key) => Records.FirstOrDefault(r => r.Key == key);
    // The player's best in one game, for comparing a match in progress against.
    public int BestKills(string game) =>
        Matches.Where(m => string.Equals(m.Game, game, StringComparison.OrdinalIgnoreCase)).Select(m => m.Kills).DefaultIfEmpty().Max();
}

// Builds the player's records from everything the engine saw: the timelines of recordings (every
// moment, and the game's own totals from its API or scoreboard), and clips from recordings made
// before timelines existed or brought in from elsewhere.
internal static class PlayerRecords
{
    // Kills this close together count as one burst (a multi-kill).
    public const double BurstWindow = 10;

    public static RecordBook Build(IEnumerable<Timeline> timelines, IEnumerable<ClipFile> clips)
    {
        var matches = new List<MatchStats>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeline in timelines)
        {
            if (timeline.Start is not { } start) continue;
            covered.Add($"{timeline.Game}|{timeline.Session}");
            var numbers = timeline.Moments.Select(m => m.Match).Concat(timeline.Scores.Select(s => s.Match)).Distinct();
            foreach (var number in numbers)
            {
                var moments = timeline.Moments.Where(m => m.Match == number).Select(m => (m.Seconds, m.Type, m.Text)).ToList();
                var scores = timeline.Scores.Where(s => s.Match == number).ToList();
                var first = moments.Select(m => m.Seconds).Concat(scores.Select(s => s.Seconds)).DefaultIfEmpty().Min();
                var stats = Summarize(timeline.Game, start.AddSeconds(first), moments, scores.Select(s => s.Stats));
                if (stats.HasCombat) matches.Add(stats);
            }
        }

        // Clips from recordings without a timeline: their moments, counted once across overlapping clips.
        var rest = clips.Where(c => !(c.Metadata is { HasSession: true } m && covered.Contains($"{c.Game}|{m.Session}")));
        foreach (var group in MatchGrouping.Group(rest))
        {
            var stats = Summarize(group.Game, group.Start, group.Events.Select(e => (e.Seconds, e.Type, e.Text)).ToList(), []);
            if (stats.HasCombat) matches.Add(stats);
        }

        matches.Sort((a, b) => b.Start.CompareTo(a.Start));
        return new RecordBook { Matches = matches, Records = FindRecords(matches), Games = Totals(matches) };
    }

    public static MatchStats Summarize(string game, DateTime start, IReadOnlyList<(double Seconds, string Type, string Text)> moments,
                                       IEnumerable<IReadOnlyDictionary<string, int>> scoreboards)
    {
        var ordered = moments.OrderBy(m => m.Seconds).ToList();
        int Count(string type) => ordered.Count(m => m.Type == type);

        // The game's totals only go up during a match, so the highest of each is the latest true one.
        var board = new Dictionary<string, int>();
        foreach (var read in scoreboards)
            foreach (var (key, value) in read)
                board[key] = Math.Max(board.GetValueOrDefault(key), value);
        int Best(string type, string stat) => Math.Max(Count(type), board.GetValueOrDefault(stat));

        int streak = 0, bestStreak = 0, bestBurst = 0;
        var recentKills = new Queue<double>();
        foreach (var m in ordered)
        {
            if (m.Type == "kill")
            {
                bestStreak = Math.Max(bestStreak, ++streak);
                recentKills.Enqueue(m.Seconds);
                while (m.Seconds - recentKills.Peek() > BurstWindow) recentKills.Dequeue();
                bestBurst = Math.Max(bestBurst, recentKills.Count);
            }
            else if (m.Type == "death")
            {
                streak = 0;
                recentKills.Clear();
            }
        }

        var extra = board.Where(kv => kv.Key is not ("kills" or "deaths" or "assists" or "revives"))
                         .ToDictionary(kv => kv.Key, kv => kv.Value);
        var result = ordered.LastOrDefault(m => m.Type == "match_end").Text;
        return new MatchStats
        {
            Game = game,
            Start = start,
            Kills = Best("kill", "kills"),
            Deaths = Best("death", "deaths"),
            Assists = Best("assist", "assists"),
            Revives = Best("revive", "revives"),
            Highlights = Count("highlight"),
            BestStreak = bestStreak,
            BestBurst = bestBurst,
            Result = string.IsNullOrWhiteSpace(result) ? null : result,
            Extra = extra,
            FromScoreboard = board.Count > 0,
        };
    }

    private static IReadOnlyList<PlayerRecord> FindRecords(IReadOnlyList<MatchStats> matches)
    {
        var records = new List<PlayerRecord>();
        void Add(string key, string title, Func<MatchStats, int> value, Func<int, string> text, Func<MatchStats, bool>? only = null)
        {
            // Ties go to the first time it was done.
            var best = matches.Where(m => only?.Invoke(m) ?? true).OrderByDescending(value).ThenBy(m => m.Start).FirstOrDefault();
            if (best is null || value(best) <= 0) return;
            records.Add(new PlayerRecord(key, title, value(best), text(value(best)), best.Game, best.Start));
        }

        Add("kills", "Most kills in a match", m => m.Kills, v => Plural(v, "kill"));
        Add("streak", "Longest kill streak", m => m.BestStreak, v => $"{Plural(v, "kill")} without dying");
        Add("burst", "Biggest multi-kill", m => m.BestBurst, v => $"{Plural(v, "kill")} in {BurstWindow:0} seconds", m => m.BestBurst >= 2);
        Add("assists", "Most assists in a match", m => m.Assists, v => Plural(v, "assist"));
        Add("revives", "Most revives in a match", m => m.Revives, v => Plural(v, "revive"));
        Add("flawless", "Most kills without dying", m => m.Deaths == 0 ? m.Kills : 0, v => $"{Plural(v, "kill")}, 0 deaths");
        // K/D only means something over a real match.
        Add("kd", "Best K/D in a match", m => (int)Math.Round(m.KillDeathRatio * 100), v => $"{v / 100.0:0.##} K/D",
            m => m.Kills >= 5);
        Add("highlights", "Most highlights in a match", m => m.Highlights, v => Plural(v, "highlight"));

        // Across matches: the most kills on one day, and the longest run of wins.
        var byDay = matches.GroupBy(m => m.Start.Date).Select(g => (Day: g.Key, Kills: g.Sum(m => m.Kills),
                                                                    Game: g.GroupBy(m => m.Game).MaxBy(x => x.Sum(m => m.Kills))!.Key))
                           .OrderByDescending(d => d.Kills).ThenBy(d => d.Day).FirstOrDefault();
        if (byDay.Kills > 0)
            records.Add(new PlayerRecord("day", "Most kills in a day", byDay.Kills, Plural(byDay.Kills, "kill"), byDay.Game, byDay.Day));

        int run = 0, bestRun = 0;
        MatchStats? runEnd = null;
        foreach (var m in matches.Where(m => m.Won || m.Lost).OrderBy(m => m.Start))
        {
            run = m.Won ? run + 1 : 0;
            if (run > bestRun)
            {
                bestRun = run;
                runEnd = m;
            }
        }
        if (bestRun >= 2 && runEnd is not null)
            records.Add(new PlayerRecord("wins", "Longest win streak", bestRun, Plural(bestRun, "win"), runEnd.Game, runEnd.Start));
        return records;
    }

    private static IReadOnlyList<GameTotals> Totals(IReadOnlyList<MatchStats> matches) =>
        matches.GroupBy(m => m.Game, StringComparer.OrdinalIgnoreCase)
               .Select(g => new GameTotals(g.First().Game, g.Count(), g.Count(m => m.Won), g.Sum(m => m.Kills), g.Sum(m => m.Deaths),
                                           g.Sum(m => m.Assists), g.Sum(m => m.Revives), g.Max(m => m.Kills)))
               .OrderByDescending(t => t.Matches).ThenBy(t => t.Game)
               .ToList();

    public static string Plural(int n, string word, string? plural = null) =>
        n == 1 ? $"1 {word}" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} {plural ?? word + "s"}";
}
