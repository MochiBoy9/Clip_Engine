using System.Globalization;
using Avalonia.Media;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// The Records page: lifetime totals, personal bests across every game, totals per game, recent
// matches, and the match being played right now set against the player's best.
public sealed class RecordsViewModel : ObservableObject
{
    private const int ShownMatches = 12;

    private RecordBook _book = new();
    private string? _liveGame;
    private int _liveKills, _liveDeaths, _liveAssists, _liveRevives;
    // The game's own totals for the live match, when it showed them.
    private Dictionary<string, int> _liveBoard = [];

    internal RecordsViewModel(MainViewModel main)
    {
        Main = main;
    }

    public MainViewModel Main { get; }

    internal RecordBook Book => _book;

    public IReadOnlyList<RecordCard> Records { get; private set; } = [];
    public IReadOnlyList<GameTotalsRow> GameTotals { get; private set; } = [];
    public IReadOnlyList<MatchRow> RecentMatches { get; private set; } = [];
    public IReadOnlyList<TotalTile> Totals { get; private set; } = [];

    public bool HasRecords => _book.Matches.Count > 0;
    public bool HasNoRecords => !HasRecords;
    public string Summary => _book.Matches.Count == 0
        ? "Play a match with Clip Engine running and your records start here."
        : $"From {PlayerRecords.Plural(_book.Matches.Count, "match", "matches")} across {PlayerRecords.Plural(_book.Games.Count, "game")}. "
          + "Every kill the engine sees counts, clipped or not, and games' own scoreboards keep the numbers exact.";

    internal void SetBook(RecordBook book)
    {
        _book = book;
        Records = book.Records.Select(r => new RecordCard(r)).ToList();
        GameTotals = book.Games.Select(g => new GameTotalsRow(g)).ToList();
        RecentMatches = book.Matches.Take(ShownMatches).Select(m => new MatchRow(m, IsRecordMatch(m))).ToList();
        Totals =
        [
            new TotalTile(Count(book.Kills), "Kills", EventKinds.Brush("kill")),
            new TotalTile(Count(book.Deaths), "Deaths", EventKinds.Brush("death")),
            new TotalTile(Count(book.Assists), "Assists", EventKinds.Brush("assist")),
            new TotalTile(Count(book.Revives), "Revives", EventKinds.Brush("revive")),
            new TotalTile(book.KillDeathRatio.ToString("0.00", CultureInfo.CurrentCulture), "K/D", EventKinds.Brush("highlight")),
            new TotalTile(Count(book.Wins), book.Wins == 1 ? "Win" : "Wins", EventKinds.Brush("match_end")),
        ];
        Raise(nameof(Records));
        Raise(nameof(GameTotals));
        Raise(nameof(RecentMatches));
        Raise(nameof(Totals));
        Raise(nameof(HasRecords));
        Raise(nameof(HasNoRecords));
        Raise(nameof(Summary));
        RaiseLive();
    }

    private bool IsRecordMatch(MatchStats m) =>
        _book.Records.Any(r => r.When == m.Start && string.Equals(r.Game, m.Game, StringComparison.OrdinalIgnoreCase) && r.Key != "day");

    // ---------------------------------------------------------------- The match being played

    public bool HasLiveMatch => _liveGame is not null;
    public string LiveTitle => _liveGame is null ? "" : $"This match · {_liveGame}";
    public string LiveKills => Count(LiveValue("kills", _liveKills));
    public string LiveDeaths => Count(LiveValue("deaths", _liveDeaths));
    public string LiveAssists => Count(LiveValue("assists", _liveAssists));
    public string LiveRevives => Count(LiveValue("revives", _liveRevives));
    public bool LiveFromScoreboard => _liveBoard.Count > 0;
    public bool IsLiveRecord => _liveGame is not null && LiveValue("kills", _liveKills) > _book.BestKills(_liveGame) &&
                                _book.BestKills(_liveGame) > 0;

    public string LiveComparison
    {
        get
        {
            if (_liveGame is null) return "";
            var best = _book.BestKills(_liveGame);
            var kills = LiveValue("kills", _liveKills);
            if (best == 0) return $"Your first {_liveGame} record is being set right now.";
            if (kills > best) return $"New {_liveGame} record! Your best was {PlayerRecords.Plural(best, "kill")}.";
            if (kills == best) return $"Level with your {_liveGame} best of {PlayerRecords.Plural(best, "kill")}.";
            return $"{PlayerRecords.Plural(best - kills, "kill")} from your {_liveGame} best of {best}.";
        }
    }

    private int LiveValue(string stat, int counted) => Math.Max(counted, _liveBoard.GetValueOrDefault(stat));

    // A new recording or a new match starts the count again.
    internal void StartLiveMatch(string? game)
    {
        _liveGame = game;
        _liveKills = _liveDeaths = _liveAssists = _liveRevives = 0;
        _liveBoard = [];
        RaiseLive();
    }

    internal void AddLiveMoment(string game, string type)
    {
        if (_liveGame is null || !string.Equals(game, _liveGame, StringComparison.OrdinalIgnoreCase)) return;
        switch (type)
        {
            case "match_start": StartLiveMatch(_liveGame); return;
            case "kill": _liveKills++; break;
            case "death": _liveDeaths++; break;
            case "assist": _liveAssists++; break;
            case "revive": _liveRevives++; break;
            default: return;
        }
        RaiseLive();
    }

    internal void SetLiveScoreboard(string game, IReadOnlyDictionary<string, int> stats)
    {
        if (_liveGame is null || !string.Equals(game, _liveGame, StringComparison.OrdinalIgnoreCase)) return;
        // A lower kill count than before means the game moved on to a new match.
        if (stats.TryGetValue("kills", out var kills) && kills < _liveBoard.GetValueOrDefault("kills"))
        {
            _liveKills = _liveDeaths = _liveAssists = _liveRevives = 0;
        }
        _liveBoard = new Dictionary<string, int>(stats);
        RaiseLive();
    }

    private void RaiseLive()
    {
        Raise(nameof(HasLiveMatch));
        Raise(nameof(LiveTitle));
        Raise(nameof(LiveKills));
        Raise(nameof(LiveDeaths));
        Raise(nameof(LiveAssists));
        Raise(nameof(LiveRevives));
        Raise(nameof(LiveFromScoreboard));
        Raise(nameof(IsLiveRecord));
        Raise(nameof(LiveComparison));
    }

    private static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}

public sealed record TotalTile(string Value, string Label, IBrush Brush);

public sealed class RecordCard
{
    internal RecordCard(PlayerRecord record)
    {
        Title = record.Title;
        Value = record.Value.ToString("N0", CultureInfo.CurrentCulture);
        if (record.Key == "kd") Value = (record.Value / 100.0).ToString("0.##", CultureInfo.CurrentCulture);
        // The big number is already there, so only the words after it ("kills without dying").
        var number = record.ValueText.Split(' ')[0];
        ValueText = record.Key != "kd" && record.ValueText.Contains(' ') && number.Any(char.IsDigit)
            ? record.ValueText[(number.Length + 1)..]
            : record.ValueText;
        Detail = record.Detail;
        Gradient = Palettes.Gradient(Palettes.IndexFor(record.Game));
    }

    public string Title { get; }
    public string Value { get; }
    public string ValueText { get; }
    public string Detail { get; }
    public IBrush Gradient { get; }
}

public sealed class GameTotalsRow
{
    internal GameTotalsRow(GameTotals totals)
    {
        Game = totals.Game;
        Initials = Format.Initials(totals.Game);
        Gradient = Palettes.Gradient(Palettes.IndexFor(totals.Game));
        Matches = PlayerRecords.Plural(totals.Matches, "match", "matches");
        Wins = totals.Wins > 0 ? PlayerRecords.Plural(totals.Wins, "win") : "";
        Kills = totals.Kills.ToString("N0", CultureInfo.CurrentCulture);
        Deaths = totals.Deaths.ToString("N0", CultureInfo.CurrentCulture);
        Assists = totals.Assists.ToString("N0", CultureInfo.CurrentCulture);
        KillDeath = totals.KillDeathRatio.ToString("0.00", CultureInfo.CurrentCulture);
        Best = PlayerRecords.Plural(totals.BestKills, "kill");
    }

    public string Game { get; }
    public string Initials { get; }
    public IBrush Gradient { get; }
    public string Matches { get; }
    public string Wins { get; }
    public string Kills { get; }
    public string Deaths { get; }
    public string Assists { get; }
    public string KillDeath { get; }
    public string Best { get; }
    public string Caption => Wins.Length > 0 ? $"{Matches} · {Wins}" : Matches;
}

public sealed class MatchRow
{
    internal MatchRow(MatchStats match, bool isRecord)
    {
        Game = match.Game;
        Initials = Format.Initials(match.Game);
        Gradient = Palettes.Gradient(Palettes.IndexFor(match.Game));
        When = Format.When(match.Start);
        Line = $"{match.Kills} / {match.Deaths} / {match.Assists}";
        Result = match.Result;
        IsWin = match.Won;
        IsLoss = match.Lost;
        IsRecord = isRecord;
        Source = match.FromScoreboard ? "From the game's scoreboard" : "From detected moments";
    }

    public string Game { get; }
    public string Initials { get; }
    public IBrush Gradient { get; }
    public string When { get; }
    public string Line { get; }
    public string? Result { get; }
    public bool HasResult => Result is not null;
    public bool IsWin { get; }
    public bool IsLoss { get; }
    public bool IsRecord { get; }
    public string Source { get; }
}
