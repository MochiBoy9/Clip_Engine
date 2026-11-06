using System.Globalization;
using System.Text;

namespace ClipEngine.Desktop.Services;

// The adaptive journal: in games built around runs (raids, missions, matches you extract
// from), the story of each run, from the journal notes the engine writes into its timelines
// (src/detect/Journal.h, and Hunt: Showdown 1896's own match summaries). Which map you picked,
// whether you went in, the enemies you destroyed and the players you downed, what you picked
// up and carried, and how it ended, with what you lost if you died. Then, across runs, your
// shortest and longest, extraction rate and records. The words follow the game: raids and ARC
// in ARC Raiders, hunters in Hunt, missions in Helldivers 2.

internal enum RunOutcome { Extracted, Died, Failed, Left, NotEntered, Unknown }

internal sealed record JournalItem(string Name, int Count);
internal sealed record JournalEnemy(string Name, int Count, int Xp);

// How a game talks about its runs, for the story.
internal sealed record JournalWords(string Run, string Runs, string Enemies, string EnemyVerb, string Player, string Players,
                                    string PlayerVerb, string Extracted)
{
    public static JournalWords For(string game) => game.ToLowerInvariant() switch
    {
        "arc raiders" => new("raid", "raids", "ARC", "Destroyed", "raider", "raiders", "Downed", "Extracted"),
        "escape from tarkov" => new("raid", "raids", "enemies", "Killed", "player", "players", "Killed", "Survived"),
        "hunt: showdown 1896" => new("match", "matches", "monsters", "Killed", "hunter", "hunters", "Took down", "Extracted"),
        "helldivers 2" => new("mission", "missions", "enemies", "Killed", "player", "players", "Killed", "Extracted"),
        "deep rock galactic" => new("mission", "missions", "creatures", "Killed", "player", "players", "Killed", "Made it out"),
        _ => new("run", "runs", "enemies", "Killed", "player", "players", "Killed", "Extracted"),
    };
}

internal sealed class JournalRun
{
    public required string Id { get; init; }      // game|session|start, for what the player logs by hand
    public required string Game { get; init; }
    public required DateTime Start { get; init; }
    public DateTime End { get; init; }            // the last thing seen in it
    public TimeSpan? Duration { get; init; }      // null when it couldn't be told
    public bool DurationApproximate { get; init; } // the start wasn't seen, only the first thing in it
    public string? Map { get; init; }
    public IReadOnlyList<string> Conditions { get; init; } = [];
    public string? Mission { get; init; }
    public RunOutcome Outcome { get; init; }
    public IReadOnlyList<JournalEnemy> Enemies { get; init; } = [];
    public int NotedPlayerKills { get; init; }
    public int LoggedPlayerKills { get; init; }   // added or taken away by the player on the Journal page
    public int Downs { get; init; }
    public IReadOnlyList<JournalItem> Loadout { get; init; } = [];  // in the inventory before going in
    public IReadOnlyList<JournalItem> Loot { get; init; } = [];     // picked up during it
    public IReadOnlyList<JournalItem> Carried { get; init; } = [];  // everything you had on you by the end
    public IReadOnlyList<JournalItem> Lost { get; init; } = [];     // lost by dying
    public int Xp { get; init; }
    public IReadOnlyList<string> Objectives { get; init; } = [];
    public IReadOnlyList<string> Other { get; init; } = [];
    // What made this run stand out among the game's runs, like "Your longest raid yet".
    public List<string> Highlights { get; } = [];
    // The clips saved during it, oldest first.
    public IReadOnlyList<ClipFile> Clips { get; set; } = [];

    public int EnemyCount => Enemies.Sum(e => e.Count);
    public int PlayerKills => Math.Max(0, NotedPlayerKills + LoggedPlayerKills);
    public bool Entered => Outcome != RunOutcome.NotEntered;
    public bool Ended => Outcome is RunOutcome.Extracted or RunOutcome.Died or RunOutcome.Failed or RunOutcome.Left;
    public JournalWords Words => JournalWords.For(Game);
}

// Ended: the runs there whose result was seen, which the extraction rate is out of, as for the game's.
internal sealed record JournalPlace(string Name, int Runs, int Extracted, int Ended)
{
    public double ExtractionRate => Ended == 0 ? 0 : (double)Extracted / Ended;
}

internal sealed class JournalGame
{
    public required string Game { get; init; }
    public JournalWords Words => JournalWords.For(Game);
    public IReadOnlyList<JournalRun> Runs { get; init; } = [];  // newest first
    public int Entered => Runs.Count(r => r.Entered);
    public int Extracted => Runs.Count(r => r.Outcome == RunOutcome.Extracted);
    public int Died => Runs.Count(r => r.Outcome == RunOutcome.Died);
    public int NotEntered => Runs.Count(r => !r.Entered);
    public int Ended => Runs.Count(r => r.Ended);
    public double ExtractionRate => Ended == 0 ? 0 : (double)Extracted / Ended;
    public int EnemyCount => Runs.Sum(r => r.EnemyCount);
    public int PlayerKills => Runs.Sum(r => r.PlayerKills);
    public int Xp => Runs.Sum(r => r.Xp);
    public int ItemsLost => Runs.Sum(r => r.Lost.Sum(i => i.Count));
    public int ItemsFound => Runs.Sum(r => r.Loot.Sum(i => i.Count));
    public JournalRun? Shortest { get; init; }
    public JournalRun? Longest { get; init; }
    public JournalRun? MostEnemies { get; init; }
    public JournalRun? MostXp { get; init; }
    public int BestExtractionStreak { get; init; }
    public IReadOnlyList<JournalPlace> Maps { get; init; } = [];        // most played first
    public IReadOnlyList<JournalEnemy> EnemyTypes { get; init; } = [];  // most destroyed first
    public IReadOnlyList<JournalItem> MostLost { get; init; } = [];     // across every run, most first
    public IReadOnlyList<JournalItem> MostFound { get; init; } = [];
    // How the last few runs went, against the extraction rate over all of them.
    public int RecentEnded { get; init; }
    public int RecentExtracted { get; init; }
    public double RecentExtractionRate => RecentEnded == 0 ? 0 : (double)RecentExtracted / RecentEnded;
}

internal sealed class JournalBook
{
    public IReadOnlyList<JournalGame> Games { get; init; } = [];  // most played first
    public IReadOnlyList<JournalRun> Runs => Games.SelectMany(g => g.Runs).OrderByDescending(r => r.Start).ToList();
    public JournalGame? Find(string game) => Games.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
}

internal static class Journal
{
    // A map picked, or a loadout seen, this long before going in still belongs to that run.
    private const double PickedWithin = 15 * 60;
    // A raid's results screen, read again soon after it ended, isn't another run.
    private const double ResultsFor = 3 * 60;
    // Inventory lines this far apart are separate looks at it.
    private const double InventoryLook = 10;
    // A run this short can't really have been timed.
    private const double ShortestTimed = 10;
    // A clip is saved a few seconds after its moment, so one saved this soon after a run ended is still from it.
    private static readonly TimeSpan ClipSavedWithin = TimeSpan.FromSeconds(30);
    // How many of the latest ended runs make up "lately".
    public const int RecentRuns = 10;

    public static JournalBook Build(IEnumerable<Timeline> timelines, IReadOnlyDictionary<string, int>? logged = null,
                                    IEnumerable<ClipFile>? clips = null)
    {
        var runs = new List<JournalRun>();
        foreach (var timeline in timelines)
        {
            if (timeline.Notes.Count == 0 || timeline.Start is not { } start) continue;
            runs.AddRange(RunsOf(timeline, start, logged ?? new Dictionary<string, int>()));
        }
        if (clips is not null)
        {
            var byGame = clips.GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Created).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var run in runs.Where(r => r.Entered))
            {
                if (!byGame.TryGetValue(run.Game, out var saved)) continue;
                run.Clips = saved.Where(c => c.Created >= run.Start && c.Created <= run.End + ClipSavedWithin).ToList();
            }
        }

        var games = new List<JournalGame>();
        foreach (var group in runs.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.OrderByDescending(r => r.Start).ToList();
            var timed = list.Where(r => r.Entered && r.Ended && r.Duration is not null).ToList();
            var shortest = timed.OrderBy(r => r.Duration).ThenBy(r => r.Start).FirstOrDefault();
            var longest = timed.OrderByDescending(r => r.Duration).ThenBy(r => r.Start).FirstOrDefault();
            var mostEnemies = list.Where(r => r.EnemyCount > 0).OrderByDescending(r => r.EnemyCount).ThenBy(r => r.Start).FirstOrDefault();
            var mostXp = list.Where(r => r.Xp > 0).OrderByDescending(r => r.Xp).ThenBy(r => r.Start).FirstOrDefault();

            int streak = 0, bestStreak = 0;
            JournalRun? streakEnd = null;
            foreach (var run in list.Where(r => r.Ended).OrderBy(r => r.Start))
            {
                streak = run.Outcome == RunOutcome.Extracted ? streak + 1 : 0;
                if (streak > bestStreak)
                {
                    bestStreak = streak;
                    streakEnd = run;
                }
            }

            var words = JournalWords.For(group.Key);
            // A record needs something to beat: one run is every record at once.
            if (timed.Count >= 2)
            {
                longest?.Highlights.Add($"Your longest {words.Run} yet.");
                if (shortest != longest) shortest?.Highlights.Add($"Your shortest {words.Run} on record.");
            }
            if (list.Count(r => r.EnemyCount > 0) >= 2)
                mostEnemies?.Highlights.Add($"The most {words.Enemies} you've {words.EnemyVerb.ToLowerInvariant()} in one {words.Run}.");
            if (bestStreak >= 2) streakEnd?.Highlights.Add($"{bestStreak} {words.Runs} in a row without dying.");
            // Making it out of a map for the first time, after trying it before.
            foreach (var map in list.Where(r => r.Map is not null && r.Ended).GroupBy(r => r.Map!, StringComparer.OrdinalIgnoreCase))
            {
                var tries = map.OrderBy(r => r.Start).ToList();
                var first = tries.FindIndex(r => r.Outcome == RunOutcome.Extracted);
                if (first > 0) tries[first].Highlights.Add($"Your first time making it out of {tries[first].Map}, after {PlayerRecords.Plural(first, "try", "tries")}.");
            }
            var recent = list.Where(r => r.Ended).Take(RecentRuns).ToList();

            games.Add(new JournalGame
            {
                Game = list[0].Game,
                Runs = list,
                Shortest = shortest,
                Longest = longest,
                MostEnemies = mostEnemies,
                MostXp = mostXp,
                BestExtractionStreak = bestStreak,
                Maps = list.Where(r => r.Entered && r.Map is not null)
                           .GroupBy(r => r.Map!, StringComparer.OrdinalIgnoreCase)
                           .Select(g => new JournalPlace(g.First().Map!, g.Count(), g.Count(r => r.Outcome == RunOutcome.Extracted), g.Count(r => r.Ended)))
                           .OrderByDescending(p => p.Runs).ThenBy(p => p.Name).ToList(),
                EnemyTypes = list.SelectMany(r => r.Enemies)
                                 .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                                 .Select(g => new JournalEnemy(g.First().Name, g.Sum(e => e.Count), g.Sum(e => e.Xp)))
                                 .OrderByDescending(e => e.Count).ThenBy(e => e.Name).ToList(),
                MostLost = Tally(list.SelectMany(r => r.Lost)),
                MostFound = Tally(list.SelectMany(r => r.Loot)),
                RecentEnded = recent.Count,
                RecentExtracted = recent.Count(r => r.Outcome == RunOutcome.Extracted),
            });
        }
        games.Sort((a, b) => b.Runs.Count != a.Runs.Count ? b.Runs.Count.CompareTo(a.Runs.Count)
                                                            : b.Runs[0].Start.CompareTo(a.Runs[0].Start));
        return new JournalBook { Games = games };
    }

    private static List<JournalItem> Tally(IEnumerable<JournalItem> items) =>
        items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
             .Select(g => new JournalItem(g.First().Name, g.Sum(i => i.Count)))
             .OrderByDescending(i => i.Count).ThenBy(i => i.Name).ToList();

    // ---------------------------------------------------------------- One recording's runs

    private sealed class Items
    {
        private readonly List<(string Name, int Count)> _items = [];
        public bool Any => _items.Count > 0;
        public void Add(string name, int count)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var i = _items.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _items[i] = (_items[i].Name, _items[i].Count + count);
            else _items.Add((name, count));
        }
        // An inventory line seen once more in the same look is the same stack, read again.
        public void See(string name, int count)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var i = _items.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _items[i] = (_items[i].Name, Math.Max(_items[i].Count, count));
            else _items.Add((name, count));
        }
        public void Clear() => _items.Clear();
        public List<JournalItem> List() => _items.Select(x => new JournalItem(x.Name, x.Count)).ToList();
        public static List<JournalItem> Union(params Items[] sets)
        {
            var all = new Items();
            foreach (var set in sets)
                foreach (var (name, count) in set._items) all.See(name, count);
            return all.List();
        }
    }

    private sealed class Pending
    {
        public string? Map;
        public double MapAt = double.NegativeInfinity;
        public List<string> Conditions = [];
        public string? Mission;
        public Items Loadout = new();
        public double LoadoutAt = double.NegativeInfinity;
        public double? QueuedAt;
    }

    private sealed class Draft
    {
        public double Start;
        public double End;
        public bool StartSeen;
        public string? Map;
        public List<string> Conditions = [];
        public string? Mission;
        public RunOutcome? Outcome;
        public readonly List<(string Name, int Count, int Xp)> Enemies = [];
        public int PlayerKills, Downs;
        public Items Loadout = new(), Loot = new(), Carried = new(), Lost = new();
        public double LastInventory = double.NegativeInfinity;
        // The results screen's XP lines, by what each line is for (SCAVENGED, TOTAL XP), at the most each read showed:
        // a screen read again, or counting up as it opens, is the same line.
        public readonly Dictionary<string, int> XpByLine = new(StringComparer.OrdinalIgnoreCase);
        public int XpLines;
        // Kill tallies on its results (enemy|Enemy, player_kill|), at the most a read showed, as for XP.
        public readonly Dictionary<string, int> Tallies = new(StringComparer.OrdinalIgnoreCase);
        public int Xp => XpByLine.Count == 0 ? XpLines
            : XpByLine.Where(x => x.Key.Contains("TOTAL", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).DefaultIfEmpty().Max() is > 0 and var total
                ? total : XpByLine.Values.Sum();
        public readonly List<string> Objectives = [], Other = [];
    }

    private static List<JournalRun> RunsOf(Timeline timeline, DateTime start, IReadOnlyDictionary<string, int> logged)
    {
        var drafts = new List<Draft>();
        var pending = new Pending();
        Draft? run = null;
        Draft? last = null;  // the run that ended last, for the results screen after it

        JournalRun Finish(Draft draft)
        {
            var id = $"{timeline.Game}|{timeline.Session}|{draft.Start.ToString("0", CultureInfo.InvariantCulture)}";
            foreach (var (key, tally) in draft.Tallies)
            {
                if (key.StartsWith("enemy|", StringComparison.Ordinal)) AddEnemy(draft, key["enemy|".Length..], tally, 0);
                else draft.PlayerKills += tally;
            }
            var outcome = draft.Outcome ?? RunOutcome.Unknown;
            var seconds = draft.End - draft.Start;
            TimeSpan? duration = outcome != RunOutcome.NotEntered && seconds >= ShortestTimed ? TimeSpan.FromSeconds(seconds) : null;
            var lost = draft.Lost.Any ? draft.Lost.List()
                : outcome == RunOutcome.Died ? Items.Union(draft.Carried, draft.Loadout, draft.Loot) : [];
            return new JournalRun
            {
                Id = id,
                Game = timeline.Game,
                Start = start.AddSeconds(draft.Start),
                End = start.AddSeconds(draft.End),
                Duration = duration,
                DurationApproximate = !draft.StartSeen,
                Map = draft.Map,
                Conditions = draft.Conditions,
                Mission = draft.Mission,
                Outcome = outcome,
                Enemies = draft.Enemies.Select(e => new JournalEnemy(e.Name, e.Count, e.Xp)).ToList(),
                NotedPlayerKills = draft.PlayerKills,
                LoggedPlayerKills = logged.GetValueOrDefault(id),
                Downs = draft.Downs,
                Loadout = draft.Loadout.List(),
                Loot = draft.Loot.List(),
                Carried = Items.Union(draft.Carried, draft.Loadout, draft.Loot),
                Lost = lost,
                Xp = draft.Xp,
                Objectives = draft.Objectives,
                Other = draft.Other,
            };
        }

        Draft Open(double t, bool startSeen)
        {
            var draft = new Draft { Start = t, End = t, StartSeen = startSeen };
            if (t - pending.MapAt <= PickedWithin)
            {
                draft.Map = pending.Map;
                draft.Conditions = pending.Conditions;
                draft.Mission = pending.Mission;
            }
            if (t - pending.LoadoutAt <= PickedWithin) draft.Loadout = pending.Loadout;
            pending = new Pending();
            return draft;
        }

        void Close(Draft draft, RunOutcome outcome, double t)
        {
            draft.Outcome = outcome;
            draft.End = Math.Max(draft.End, t);
            drafts.Add(draft);
            last = draft;
            run = null;
        }

        // The run that just ended, for its results screen read after it: soon after, and before the player queued again.
        Draft? Results(double t) => run is null && last is not null && t - last.End <= ResultsFor && pending.QueuedAt is null ? last : null;

        void AddEnemy(Draft draft, string name, int count, int xp)
        {
            var i = draft.Enemies.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) draft.Enemies[i] = (draft.Enemies[i].Name, draft.Enemies[i].Count + count, draft.Enemies[i].Xp + xp);
            else draft.Enemies.Add((name, count, xp));
        }

        // A queue that never turned into a run: the player backed out.
        void Abandon(double t)
        {
            if (pending.QueuedAt is not { } queued) return;
            var draft = new Draft { Start = queued, End = t, Map = pending.Map, Conditions = pending.Conditions, Mission = pending.Mission };
            draft.Outcome = RunOutcome.NotEntered;
            drafts.Add(draft);
            pending.QueuedAt = null;
        }

        foreach (var note in timeline.Notes.OrderBy(n => n.Seconds))
        {
            var t = note.Seconds;
            var value = note.Value.Trim();
            var count = Math.Max(1, note.Amount);
            switch (note.Note)
            {
                case "map":
                    if (value.Length == 0) break;
                    // The map screen opened during the raid.
                    if (run is not null)
                    {
                        run.Map ??= value;
                        break;
                    }
                    if (!string.Equals(pending.Map, value, StringComparison.OrdinalIgnoreCase)) pending.Conditions = [];
                    pending.Map = value;
                    pending.MapAt = t;
                    break;
                case "condition":
                    var conditions = run?.Conditions ?? pending.Conditions;
                    if (value.Length > 0 && !conditions.Contains(value, StringComparer.OrdinalIgnoreCase)) conditions.Add(value);
                    break;
                case "mission":
                    if (value.Length == 0) break;
                    if (run is not null) run.Mission ??= value;
                    else pending.Mission = value;
                    break;
                case "queue":
                    if (run is not null) break;
                    // Queued again long after: the first try was given up.
                    if (pending.QueuedAt is { } before && t - before > PickedWithin) Abandon(t);
                    pending.QueuedAt ??= t;
                    break;
                case "deploy":
                    if (run is not null)
                    {
                        // The same run's start, read again as it loads in.
                        if (t - run.Start < 90)
                        {
                            if (!run.StartSeen) run.Start = t;
                            run.StartSeen = true;
                            if (value.Length > 0) run.Mission ??= value;
                            break;
                        }
                        Close(run, RunOutcome.Unknown, run.End);
                    }
                    run = Open(t, startSeen: true);
                    if (value.Length > 0) run.Mission ??= value;
                    break;
                case "inventory":
                    if (run is not null)
                    {
                        if (t - run.LastInventory > InventoryLook) run.Carried.Clear();
                        run.Carried.See(value, count);
                        run.LastInventory = t;
                        run.End = t;
                    }
                    else
                    {
                        if (t - pending.LoadoutAt > InventoryLook) pending.Loadout.Clear();
                        pending.Loadout.See(value, count);
                        pending.LoadoutAt = t;
                    }
                    break;
                case "xp":
                case "lost":
                {
                    // XP is only noted from a results screen, so one showing means the run is over, even when how it
                    // ended was missed; the outcome read next fills that in.
                    if (note.Note == "xp" && run is not null) Close(run, RunOutcome.Unknown, t);
                    var target = run ?? Results(t);
                    if (target is null) break;
                    if (note.Note == "xp")
                    {
                        // What the line is for, without its number, even one read stuck to a word (1,100XP).
                        var line = string.Join(' ', note.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                             .Select(w => new string(w.Where(char.IsLetter).ToArray())).Where(w => w.Length > 0));
                        target.XpByLine[line] = Math.Max(target.XpByLine.GetValueOrDefault(line), note.Amount);
                    }
                    else
                    {
                        target.Lost.Add(value, count);
                    }
                    break;
                }
                case "extracted":
                case "died":
                case "failed":
                case "left":
                {
                    var outcome = note.Note switch
                    {
                        "extracted" => RunOutcome.Extracted,
                        "died" => RunOutcome.Died,
                        "failed" => RunOutcome.Failed,
                        _ => RunOutcome.Left,
                    };
                    if (run is null)
                    {
                        // The same ending read again, or how a run closed by its results screen ended.
                        if (Results(t) is { } ended)
                        {
                            if (ended.Outcome == RunOutcome.Unknown) ended.Outcome = outcome;
                            break;
                        }
                        run = Open(t, startSeen: false);
                    }
                    Close(run, outcome, t);
                    break;
                }
                default:
                    if (run is null)
                    {
                        // Its results, read after it ended. A tally there (KILLS 212) is the run's; a line naming one
                        // (DESTROYED ARC 1,100) is the same kills again, not a new run.
                        if (note.Note is "enemy" or "player_kill" or "objective" && Results(t) is { } ended)
                        {
                            if (note.Amount > 0 && note.Note != "objective")
                            {
                                var key = note.Note == "enemy" ? $"enemy|{(value.Length > 0 ? value : "Enemy")}" : "player_kill|";
                                ended.Tallies[key] = Math.Max(ended.Tallies.GetValueOrDefault(key), note.Amount);
                            }
                            break;
                        }
                        run = Open(t, startSeen: false);
                    }
                    run.End = t;
                    switch (note.Note)
                    {
                        case "enemy": AddEnemy(run, value.Length > 0 ? value : "Enemy", count, note.Xp); break;
                        case "player_kill": run.PlayerKills += count; break;
                        case "downed": run.Downs++; break;
                        case "loot": run.Loot.Add(value, count); break;
                        case "objective":
                            var objective = value.Length > 0 ? value : note.Text.Trim();
                            if (objective.Length > 0) run.Objectives.Add(objective);
                            break;
                        default:
                            run.Other.Add(value.Length > 0 ? $"{Label(note.Note)}: {value}" : Label(note.Note));
                            break;
                    }
                    break;
            }
            // XP on a line: the run's, or the one that just ended, when it's on its results ("EXTRACTED +1,200 XP").
            if (note.Xp > 0 && note.Note != "xp" && (run ?? Results(t)) is { } earner)
                earner.XpLines += note.Xp;
        }

        if (run is not null) Close(run, RunOutcome.Unknown, run.End);
        Abandon(timeline.Notes.Max(n => n.Seconds));
        return drafts.Select(Finish).ToList();
    }

    private static string Label(string note) =>
        note.Length == 0 ? note : char.ToUpperInvariant(note[0]) + note[1..].Replace('_', ' ');

    // ---------------------------------------------------------------- The story

    // The run told as a few sentences, in the game's own words.
    public static string Story(JournalRun run)
    {
        var w = run.Words;
        var story = new StringBuilder();
        void Say(string sentence)
        {
            if (story.Length > 0) story.Append(' ');
            story.Append(sentence);
        }

        var where = run.Map ?? run.Mission;
        var conditions = run.Conditions.Count > 0 ? $" ({string.Join(", ", run.Conditions)})" : "";
        var mission = run.Map is not null && run.Mission is not null ? $"{run.Mission} in " : "";
        if (!run.Entered)
        {
            Say(where is null ? $"Queued for a {w.Run} but didn't go in." : $"Picked {mission}{where}{conditions} but didn't go in.");
            return story.ToString();
        }
        Say(where is null ? $"Started a {w.Run}." : $"Went into {mission}{where}{conditions}.");
        if (run.Loadout.Count > 0) Say($"Took in {ItemList(run.Loadout, 3)}.");

        if (run.Enemies.Count > 0)
        {
            var named = run.Enemies.OrderByDescending(e => e.Count).Select(e => Counted(e.Count, e.Name)).ToList();
            Say($"{w.EnemyVerb} {Sentence(named, 4)}.");
        }
        if (run.PlayerKills > 0) Say($"{w.PlayerVerb} {Counted(run.PlayerKills, w.Player, w.Players)}.");
        if (run.Downs > 0) Say(run.Downs == 1 ? "Went down once." : $"Went down {run.Downs} times.");
        foreach (var objective in run.Objectives.Distinct(StringComparer.OrdinalIgnoreCase).Take(3)) Say($"{objective}.");
        if (run.Loot.Count > 0)
        {
            var found = run.Loot.Sum(i => i.Count);
            Say($"Picked up {Counted(found, "item")}: {ItemList(run.Loot, 3)}.");
        }

        var time = run.Duration is { } d ? (run.DurationApproximate ? "at least " : "") + Duration(d) : null;
        switch (run.Outcome)
        {
            case RunOutcome.Extracted:
                var home = run.Carried.Sum(i => i.Count);
                Say(time is null ? $"{w.Extracted}" : $"{w.Extracted} after {time}");
                story.Append(home > 0 ? $" with {Counted(home, "item")}." : ".");
                break;
            case RunOutcome.Died:
                Say(time is null ? "Died" : $"Died {time} in");
                story.Append(run.Lost.Count > 0 ? $", losing {ItemList(run.Lost, 4)}." : ".");
                break;
            case RunOutcome.Failed:
                Say(time is null ? $"The {w.Run} failed." : $"The {w.Run} failed after {time}.");
                break;
            case RunOutcome.Left:
                Say(time is null ? $"Left the {w.Run} early." : $"Left the {w.Run} after {time}.");
                break;
            default:
                Say($"The recording stopped before the {w.Run} ended.");
                break;
        }
        if (run.Xp > 0) Say($"+{run.Xp.ToString("N0", CultureInfo.CurrentCulture)} XP.");
        foreach (var highlight in run.Highlights) Say(highlight);
        return story.ToString();
    }

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    // "Metal Parts ×3, ARC Alloy and 4 more".
    public static string ItemList(IReadOnlyList<JournalItem> items, int shown)
    {
        var parts = items.Take(shown).Select(i => i.Count > 1 ? $"{i.Name} ×{i.Count}" : i.Name).ToList();
        if (items.Count > shown) parts.Add($"{items.Count - shown} more");
        return Sentence(parts, parts.Count);
    }

    // "a Wasp", "3 Wasps".
    public static string Counted(int count, string name, string? plural = null)
    {
        if (count == 1)
        {
            // "Metal Parts" is already many; "Boss" is one.
            if (plural is null && name.EndsWith('s') && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return $"1 {name}";
            return $"{("aeiou".Contains(char.ToLowerInvariant(name[0])) ? "an" : "a")} {name}";
        }
        return $"{count.ToString("N0", CultureInfo.CurrentCulture)} {plural ?? Plural(name)}";
    }

    private static string Plural(string name) =>
        name.EndsWith("ss", StringComparison.OrdinalIgnoreCase) || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase) || name.EndsWith('x') ? name + "es"
        : name.EndsWith('s') || name.EndsWith("ARC", StringComparison.Ordinal) ? name
        : name.EndsWith('y') && name.Length > 1 && !"aeiou".Contains(name[^2]) ? name[..^1] + "ies"
        : name + "s";

    private static string Sentence(IReadOnlyList<string> parts, int shown)
    {
        var list = parts.Take(shown).ToList();
        if (parts.Count > shown) list.Add($"{parts.Count - shown} more");
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
