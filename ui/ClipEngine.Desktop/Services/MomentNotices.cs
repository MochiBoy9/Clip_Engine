namespace ClipEngine.Desktop.Services;

// What the notice over the game says as moments come in: each kill with the match's count,
// multi-kills ("Triple kill") when they come within a few seconds of each other, streaks
// without dying, revives and assists, the match's line when it ends, and once a match, a new
// personal best for kills in that game. Deaths get no notice of their own; they end a streak.
internal sealed class MomentNotices
{
    // Kills this close together make a multi-kill, as games count them.
    internal static readonly TimeSpan MultiKillGap = TimeSpan.FromSeconds(4);

    private int _multi;
    private DateTime _lastKill = DateTime.MinValue;
    private int _streak;
    private bool _bestAnnounced;
    private int _kills, _deaths, _assists;
    // After a match ends, the match count passed in still holds that match until the next one
    // starts, so the kills are counted here alone.
    private bool _useMatchKills = true;

    // A new match (or recording) starts every count again.
    public void Reset()
    {
        _multi = _streak = _kills = _deaths = _assists = 0;
        _lastKill = DateTime.MinValue;
        _bestAnnounced = false;
        _useMatchKills = true;
    }

    // The notice for a moment, or null for none. matchKills is the match's kill count after
    // this moment (the scoreboard's, when the game shows one), bestKills the player's best in a
    // match of this game before it (0 for none yet).
    public string? OnMoment(string type, DateTime at, int matchKills, int bestKills)
    {
        switch (type)
        {
            case "match_start":
                Reset();
                return "Match started";
            case "kill":
                _kills++;
                _streak++;
                _multi = at - _lastKill <= MultiKillGap ? _multi + 1 : 1;
                _lastKill = at;
                var kills = _useMatchKills ? Math.Max(matchKills, _kills) : _kills;
                if (!_bestAnnounced && bestKills > 0 && kills > bestKills)
                {
                    _bestAnnounced = true;
                    return $"New personal best · {kills} kills";
                }
                if (_multi >= 2) return MultiKill(_multi);
                if (_streak >= 5 && _streak % 5 == 0) return $"{_streak} kills without dying";
                return kills == 1 ? "Kill" : $"Kill · {kills} this match";
            case "death":
                _deaths++;
                _streak = 0;
                _multi = 0;
                return null;
            case "assist":
                _assists++;
                return "Assist";
            case "revive":
                return "Revive";
            case "match_end":
                var line = $"Match over · {(_useMatchKills ? Math.Max(matchKills, _kills) : _kills)} / {_deaths} / {_assists}";
                Reset();
                _useMatchKills = false;
                return line;
            default:
                return null;
        }
    }

    internal static string MultiKill(int count) => count switch
    {
        2 => "Double kill",
        3 => "Triple kill",
        4 => "Quad kill",
        _ => $"{count} kills in a row",
    };
}
