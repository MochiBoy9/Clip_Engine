using System.Globalization;
using Avalonia.Media;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The Journal page: for each game with a journal, the story of every run, your shortest and
// longest, extraction rate, the maps you play and what you destroyed, and what you found and
// lost. Raider kills can be logged by hand on each run, for the fights the screen doesn't show.
public sealed class JournalViewModel : ObservableObject
{
    private const int ShownRuns = 30;

    private JournalBook _book = new();
    private string? _game;  // the game shown; null until there's a journal

    internal JournalViewModel(MainViewModel main)
    {
        Main = main;
        SelectGameCommand = new RelayCommand(p =>
        {
            if (p is string game) SelectGame(game);
        });
    }

    public MainViewModel Main { get; }
    public RelayCommand SelectGameCommand { get; }

    // Adds to (or takes from) a run's player kills by hand; MainViewModel keeps them.
    internal Action<string, int>? LogPlayerKills { get; set; }

    internal JournalBook Book => _book;
    private JournalGame? Shown => _game is null ? null : _book.Find(_game);

    public bool HasJournal => _book.Games.Count > 0;
    public bool HasNoJournal => !HasJournal;
    public bool HasSeveralGames => _book.Games.Count > 1;
    public IReadOnlyList<JournalGameTab> GameTabs { get; private set; } = [];
    public string GameTitle => Shown?.Game ?? "";

    public string Summary
    {
        get
        {
            if (Shown is not { } g)
                return "Play ARC Raiders, Escape from Tarkov, Hunt: Showdown 1896, Helldivers 2 or Deep Rock Galactic with Clip Engine running, and the story of every run starts here.";
            var w = g.Words;
            var skipped = g.NotEntered > 0 ? $", and {PlayerRecords.Plural(g.NotEntered, "time")} you picked a map and backed out" : "";
            return $"{Cap(PlayerRecords.Plural(g.Entered, w.Run, w.Runs))} in {g.Game}{skipped}.{Lately(g)} Every map, fight, find and loss the engine saw, run by run.";
        }
    }

    public IReadOnlyList<TotalTile> Totals { get; private set; } = [];
    public IReadOnlyList<JournalBestCard> Bests { get; private set; } = [];
    public IReadOnlyList<JournalPlaceRow> Maps { get; private set; } = [];
    public IReadOnlyList<JournalEnemyRow> Enemies { get; private set; } = [];
    public IReadOnlyList<JournalEnemyRow> MostLost { get; private set; } = [];
    public IReadOnlyList<JournalEnemyRow> MostFound { get; private set; } = [];
    public bool HasItems => MostLost.Count > 0 || MostFound.Count > 0;
    public bool HasMostLost => MostLost.Count > 0;
    public bool HasMostFound => MostFound.Count > 0;
    public IReadOnlyList<JournalRunCard> Runs { get; private set; } = [];
    public bool HasMaps => Maps.Count > 0;
    public bool HasEnemies => Enemies.Count > 0;
    public string EnemiesTitle => Shown is { } g ? $"{g.Words.Enemies.ToUpper(CultureInfo.CurrentCulture)} {g.Words.EnemyVerb.ToUpper(CultureInfo.CurrentCulture)}" : "";
    public string RunsTitle => Shown is { } g ? $"EVERY {g.Words.Run.ToUpper(CultureInfo.CurrentCulture)}" : "";

    internal void SetBook(JournalBook book)
    {
        _book = book;
        if (_game is null || book.Find(_game) is null) _game = book.Games.FirstOrDefault()?.Game;
        Update();
    }

    internal void SelectGame(string game)
    {
        if (_book.Find(game) is null) return;
        _game = game;
        Update();
    }

    private void Update()
    {
        GameTabs = _book.Games.Select(g => new JournalGameTab(g.Game, PlayerRecords.Plural(g.Runs.Count(r => r.Entered), g.Words.Run, g.Words.Runs),
                                                              string.Equals(g.Game, _game, StringComparison.OrdinalIgnoreCase),
                                                              SelectGameCommand))
                              .ToList();
        if (Shown is { } g)
        {
            var w = g.Words;
            Totals =
            [
                new TotalTile(Count(g.Entered), Cap(w.Runs), EventKinds.Brush("match_start")),
                new TotalTile(g.Ended == 0 ? "—" : g.ExtractionRate.ToString("0%", CultureInfo.CurrentCulture), w.Extracted, EventKinds.Brush("match_end")),
                new TotalTile(Count(g.EnemyCount), $"{Cap(w.Enemies)} {w.EnemyVerb.ToLowerInvariant()}", EventKinds.Brush("kill")),
                new TotalTile(Count(g.PlayerKills), $"{Cap(w.Players)} {w.PlayerVerb.ToLowerInvariant()}", EventKinds.Brush("assist")),
                new TotalTile(g.Shortest?.Duration is { } s ? Journal.Duration(s) : "—", $"Shortest {w.Run}", EventKinds.Brush("revive")),
                new TotalTile(g.Longest?.Duration is { } l ? Journal.Duration(l) : "—", $"Longest {w.Run}", EventKinds.Brush("highlight")),
            ];
            var bests = new List<JournalBestCard>();
            if (g.Longest is { Duration: { } longest } lr) bests.Add(new($"Longest {w.Run}", Journal.Duration(longest), Outcome(lr), lr));
            if (g.Shortest is { Duration: { } shortest } sr && sr != g.Longest) bests.Add(new($"Shortest {w.Run}", Journal.Duration(shortest), Outcome(sr), sr));
            if (g.MostEnemies is { } me)
                bests.Add(new($"Most {w.Enemies} in a {w.Run}", Count(me.EnemyCount), $"{w.Enemies} {w.EnemyVerb.ToLowerInvariant()}", me));
            if (g.MostXp is { } mx) bests.Add(new($"Most XP in a {w.Run}", Count(mx.Xp), "XP", mx));
            if (g.BestExtractionStreak >= 2)
            {
                var end = g.Runs.FirstOrDefault(r => r.Highlights.Any(h => h.Contains("in a row", StringComparison.Ordinal)));
                bests.Add(new("Best streak", Count(g.BestExtractionStreak), $"{w.Runs} in a row, {w.Extracted.ToLowerInvariant()}", end));
            }
            Bests = bests;
            Maps = g.Maps.Take(8).Select(p => new JournalPlaceRow(p, w)).ToList();
            var most = g.EnemyTypes.Select(e => e.Count).DefaultIfEmpty(1).Max();
            Enemies = g.EnemyTypes.Take(8).Select(e => new JournalEnemyRow(e, most)).ToList();
            MostLost = ItemRows(g.MostLost);
            MostFound = ItemRows(g.MostFound);
            Runs = g.Runs.Take(ShownRuns).Select(r => new JournalRunCard(r, this)).ToList();
        }
        else
        {
            Totals = [];
            Bests = [];
            Maps = [];
            Enemies = [];
            MostLost = [];
            MostFound = [];
            Runs = [];
        }
        Raise(nameof(HasJournal));
        Raise(nameof(HasNoJournal));
        Raise(nameof(HasSeveralGames));
        Raise(nameof(GameTabs));
        Raise(nameof(GameTitle));
        Raise(nameof(Summary));
        Raise(nameof(Totals));
        Raise(nameof(Bests));
        Raise(nameof(Maps));
        Raise(nameof(Enemies));
        Raise(nameof(HasMaps));
        Raise(nameof(HasEnemies));
        Raise(nameof(MostLost));
        Raise(nameof(MostFound));
        Raise(nameof(HasItems));
        Raise(nameof(HasMostLost));
        Raise(nameof(HasMostFound));
        Raise(nameof(EnemiesTitle));
        Raise(nameof(RunsTitle));
        Raise(nameof(Runs));
    }

    // "Lately 7 of your last 10 raids made it out, up from 52% overall."
    private static string Lately(JournalGame g)
    {
        // Only worth saying once there's more than the latest runs to compare them with.
        if (g.Ended <= g.RecentEnded || g.RecentEnded < 5) return "";
        var w = g.Words;
        var diff = g.RecentExtractionRate - g.ExtractionRate;
        var trend = Math.Abs(diff) < 0.05 ? "about your"
            : diff > 0 ? "up from" : "down from";
        return $" Lately {g.RecentExtracted} of your last {g.RecentEnded} {w.Runs} ended {w.Extracted.ToLowerInvariant()}, " +
               $"{trend} {g.ExtractionRate.ToString("0%", CultureInfo.CurrentCulture)} overall.";
    }

    private static List<JournalEnemyRow> ItemRows(IReadOnlyList<JournalItem> items)
    {
        var most = items.Select(i => i.Count).DefaultIfEmpty(1).Max();
        return items.Take(6).Select(i => new JournalEnemyRow(new JournalEnemy(i.Name, i.Count, 0), most)).ToList();
    }

    // Opens a run's clips in the player, from the first.
    internal Action<ClipFile>? OpenClip { get; set; }

    internal void ChangePlayerKills(JournalRun run, int by)
    {
        if (run.PlayerKills + by < 0) return;
        LogPlayerKills?.Invoke(run.Id, by);
    }

    private static string Outcome(JournalRun run) => run.Outcome switch
    {
        // The map is on the line under it.
        RunOutcome.Extracted => run.Words.Extracted,
        RunOutcome.Died => "Died",
        RunOutcome.Failed => $"The {run.Words.Run} failed",
        RunOutcome.Left => "Left early",
        _ => Cap(run.Words.Run),
    };

    internal static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
    internal static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
}

public sealed record JournalGameTab(string Game, string Caption, bool IsSelected, RelayCommand Select);

public sealed class JournalBestCard
{
    internal JournalBestCard(string title, string value, string valueText, JournalRun? run)
    {
        Label = title.ToUpper(CultureInfo.CurrentCulture);
        Value = value;
        ValueText = valueText;
        Detail = run is null ? "" : $"{run.Map ?? run.Game} · {run.Start.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        Gradient = Palettes.Gradient(Palettes.IndexFor(run?.Map ?? run?.Game ?? title));
    }

    public string Label { get; }
    public string Value { get; }
    public string ValueText { get; }
    public string Detail { get; }
    public IBrush Gradient { get; }
}

public sealed class JournalPlaceRow
{
    internal JournalPlaceRow(JournalPlace place, JournalWords words)
    {
        Name = place.Name;
        Initials = Format.Initials(place.Name);
        Gradient = Palettes.Gradient(Palettes.IndexFor(place.Name));
        Caption = PlayerRecords.Plural(place.Runs, words.Run, words.Runs);
        Rate = place.ExtractionRate.ToString("0%", CultureInfo.CurrentCulture);
        RateCaption = words.Extracted.ToLowerInvariant();
    }

    public string Name { get; }
    public string Initials { get; }
    public IBrush Gradient { get; }
    public string Caption { get; }
    public string Rate { get; }
    public string RateCaption { get; }
}

public sealed class JournalEnemyRow
{
    internal JournalEnemyRow(JournalEnemy enemy, int most)
    {
        Name = enemy.Name;
        Count = JournalViewModel.Count(enemy.Count);
        Xp = enemy.Xp > 0 ? $"{JournalViewModel.Count(enemy.Xp)} XP" : "";
        // The bar's length, against the most destroyed.
        Bar = 4 + 196.0 * enemy.Count / Math.Max(1, most);
    }

    public string Name { get; }
    public string Count { get; }
    public string Xp { get; }
    public double Bar { get; }
}

public sealed class JournalRunCard : ObservableObject
{
    private readonly JournalRun _run;

    internal JournalRunCard(JournalRun run, JournalViewModel owner)
    {
        _run = run;
        var w = run.Words;
        Game = run.Game;
        Title = run.Map ?? run.Mission ?? JournalViewModel.Cap(w.Run);
        Subtitle = string.Join(" · ", new[] { run.Map is not null ? run.Mission : null, run.Conditions.Count > 0 ? string.Join(", ", run.Conditions) : null }
                                      .Where(s => !string.IsNullOrEmpty(s)));
        When = Format.When(run.Start);
        Length = run.Duration is { } d ? (run.DurationApproximate ? "≥ " : "") + Journal.Duration(d) : "";
        (Outcome, IsGood, IsBad) = run.Outcome switch
        {
            RunOutcome.Extracted => (w.Extracted.ToUpper(CultureInfo.CurrentCulture), true, false),
            RunOutcome.Died => ("DIED", false, true),
            RunOutcome.Failed => ("FAILED", false, true),
            RunOutcome.Left => ("LEFT", false, false),
            RunOutcome.NotEntered => ("BACKED OUT", false, false),
            _ => ("NO RESULT", false, false),
        };
        Story = Journal.Story(run);
        EnemyChips = run.Enemies.OrderByDescending(e => e.Count).Select(e => e.Count > 1 ? $"{e.Name} ×{e.Count}" : e.Name).ToList();
        Found = run.Loot.Count > 0 ? Journal.ItemList(run.Loot, 8) : "";
        Lost = run.Lost.Count > 0 ? Journal.ItemList(run.Lost, 8) : "";
        BroughtHome = run.Outcome == RunOutcome.Extracted && run.Carried.Count > 0 ? Journal.ItemList(run.Carried, 8) : "";
        Entered = run.Entered;
        PlayerKillsLabel = JournalViewModel.Cap($"{w.Players} {w.PlayerVerb.ToLowerInvariant()}");
        IsRecord = run.Highlights.Count > 0;
        ClipsLabel = run.Clips.Count == 0 ? "" : PlayerRecords.Plural(run.Clips.Count, "clip");
        WatchClipsCommand = new RelayCommand(() => owner.OpenClip?.Invoke(_run.Clips[0]), () => _run.Clips.Count > 0);
        AddKillCommand = new RelayCommand(() => owner.ChangePlayerKills(_run, 1));
        RemoveKillCommand = new RelayCommand(() => owner.ChangePlayerKills(_run, -1), () => _run.PlayerKills > 0);
    }

    public string Game { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool HasSubtitle => Subtitle.Length > 0;
    public string When { get; }
    public string Length { get; }
    public bool HasLength => Length.Length > 0;
    public string Outcome { get; }
    public bool IsGood { get; }
    public bool IsBad { get; }
    public string Story { get; }
    public IReadOnlyList<string> EnemyChips { get; }
    public bool HasEnemies => EnemyChips.Count > 0;
    public string Found { get; }
    public bool HasFound => Found.Length > 0;
    public string Lost { get; }
    public bool HasLost => Lost.Length > 0;
    public string BroughtHome { get; }
    public bool HasBroughtHome => BroughtHome.Length > 0;
    public bool Entered { get; }
    public bool IsRecord { get; }
    public string PlayerKills => JournalViewModel.Count(_run.PlayerKills);
    public string PlayerKillsLabel { get; }
    public string PlayerKillsTip => _run.LoggedPlayerKills == 0
        ? "Seen by the engine. Use + and − to log kills it missed."
        : $"{_run.NotedPlayerKills} seen by the engine, {(_run.LoggedPlayerKills > 0 ? "+" : "")}{_run.LoggedPlayerKills} logged by you";
    public string ClipsLabel { get; }
    public bool HasClips => ClipsLabel.Length > 0;
    public RelayCommand WatchClipsCommand { get; }
    public RelayCommand AddKillCommand { get; }
    public RelayCommand RemoveKillCommand { get; }
}
