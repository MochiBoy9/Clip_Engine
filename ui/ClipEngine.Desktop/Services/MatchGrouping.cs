using System.Globalization;

namespace ClipEngine.Desktop.Services;

// A moment in a match, counted from when the match's first clip starts.
internal sealed record MatchEvent(string Type, double Seconds, string Text);

// The clips of one match (or one play session, in games without match detection), with every
// moment they caught, counted once even where clips overlap.
internal sealed class MatchGroup
{
    public required string Key { get; init; }
    public required string Game { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    // Which match of its recording this was, or 0 when no match start was detected.
    public required int Number { get; init; }
    public required IReadOnlyList<ClipFile> Clips { get; init; }  // oldest first
    public required IReadOnlyList<MatchEvent> Events { get; init; }

    public TimeSpan Length => End - Start;
    public int Count(string type) => Events.Count(e => e.Type == type);

    // What the match end said, like "VICTORY", when it was caught.
    public string? Result => Events.LastOrDefault(e => e.Type == "match_end")?.Text is { Length: > 0 } text ? text : null;
}

internal static class MatchGrouping
{
    // Clips from before matches were recorded are grouped by how close together they were saved.
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(20);

    // Two reads of the same moment in overlapping clips land within this of each other.
    private const double SameMoment = 1.0;

    public static IReadOnlyList<MatchGroup> Group(IEnumerable<ClipFile> clips)
    {
        var placed = clips.Select(c => (Clip: c, Start: StartOf(c), Key: SessionKey(c))).ToList();
        var groups = new List<MatchGroup>();

        foreach (var session in placed.Where(p => p.Key is not null).GroupBy(p => p.Key!, StringComparer.OrdinalIgnoreCase))
            groups.Add(Build(session.Key, session.Select(p => (p.Clip, p.Start)).ToList(), session.First().Clip.Metadata!.Match));

        // Older clips: one group per game per stretch of play.
        foreach (var game in placed.Where(p => p.Key is null).GroupBy(p => p.Clip.Game, StringComparer.OrdinalIgnoreCase))
        {
            var run = new List<(ClipFile Clip, DateTime Start)>();
            foreach (var p in game.OrderBy(p => p.Start))
            {
                if (run.Count > 0 && p.Start - EndOf(run[^1]) > SessionGap)
                {
                    groups.Add(Build(LegacyKey(run), run, 0));
                    run = [];
                }
                run.Add((p.Clip, p.Start));
            }
            if (run.Count > 0) groups.Add(Build(LegacyKey(run), run, 0));
        }

        groups.Sort((a, b) => b.Start.CompareTo(a.Start));
        return groups;
    }

    // When the clip's first frame was recorded.
    public static DateTime StartOf(ClipFile clip)
    {
        if (clip.Metadata is { HasSession: true } m && TryParseSession(m.Session, out var session))
            return session.AddSeconds(m.SessionSeconds);
        // The file is written when the clip is saved, right after its last frame.
        return clip.Created - Length(clip);
    }

    private static TimeSpan Length(ClipFile clip) =>
        clip.Duration ?? TimeSpan.FromSeconds(Math.Max(0, clip.Metadata?.Seconds ?? 0));

    private static DateTime EndOf((ClipFile Clip, DateTime Start) p) => p.Start + Length(p.Clip);

    private static string? SessionKey(ClipFile clip) =>
        clip.Metadata is { HasSession: true } m && TryParseSession(m.Session, out _)
            ? $"{clip.Game}|{m.Session}|{m.Match}"
            : null;

    private static string LegacyKey(List<(ClipFile Clip, DateTime Start)> run) =>
        $"{run[0].Clip.Game}|{run[0].Start.ToString("s", CultureInfo.InvariantCulture)}|0";

    private static bool TryParseSession(string session, out DateTime time) =>
        DateTime.TryParseExact(session, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                               out time);

    private static MatchGroup Build(string key, List<(ClipFile Clip, DateTime Start)> clips, int number)
    {
        clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        var start = clips[0].Start;
        var end = clips.Max(EndOf);

        var events = new List<MatchEvent>();
        foreach (var (clip, clipStart) in clips)
        {
            foreach (var e in clip.Metadata?.Events ?? [])
            {
                var seconds = (clipStart - start).TotalSeconds + e.Time;
                if (events.Any(x => x.Type == e.Type && Math.Abs(x.Seconds - seconds) < SameMoment)) continue;
                events.Add(new MatchEvent(e.Type, seconds, e.Text));
            }
        }
        events.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

        return new MatchGroup
        {
            Key = key,
            Game = clips[0].Clip.Game,
            Start = start,
            End = end,
            Number = number,
            Clips = clips.Select(c => c.Clip).ToList(),
            Events = events,
        };
    }
}
