using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class MatchGroupingTests
{
    private static ClipFile Clip(string game, DateTime saved, double seconds, ClipMetadata? metadata = null) =>
        new($@"C:\Clips\{game}\{game}_{saved:HH-mm-ss}.mp4", game, saved, 100, TimeSpan.FromSeconds(seconds), metadata);

    private static ClipMetadata Tagged(string session, double sessionSeconds, int match, params (string Type, double Time)[] events) =>
        new()
        {
            Session = session,
            SessionSeconds = sessionSeconds,
            Match = match,
            Events = events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time, Text = e.Type.ToUpperInvariant() }).ToList(),
        };

    [Fact]
    public void GroupsTaggedClipsByMatchAndCountsOverlapsOnce()
    {
        const string session = "2026-10-27T20:00:00";
        var start = new DateTime(2026, 10, 27, 20, 0, 0);
        var clips = new[]
        {
            // Two overlapping clips of match 1: the kill at 115 s into the session is in both.
            Clip("The Finals", start.AddSeconds(120), 20, Tagged(session, 100, 1, ("kill", 15), ("assist", 18))),
            Clip("The Finals", start.AddSeconds(130), 20, Tagged(session, 110, 1, ("kill", 5.3), ("kill", 12))),
            Clip("The Finals", start.AddSeconds(900), 30, Tagged(session, 870, 2, ("match_start", 1), ("death", 20))),
        };

        var groups = MatchGrouping.Group(clips);

        Assert.Equal(2, groups.Count);
        var latest = groups[0];
        Assert.Equal(2, latest.Number);
        Assert.Equal(1, latest.Count("death"));

        var first = groups[1];
        Assert.Equal(1, first.Number);
        Assert.Equal(2, first.Clips.Count);
        Assert.Equal(start.AddSeconds(100), first.Start);
        Assert.Equal(start.AddSeconds(130), first.End);
        Assert.Equal(2, first.Count("kill"));  // 115 s counted once, plus 122 s
        Assert.Equal(1, first.Count("assist"));
        Assert.Equal([15, 18, 22], first.Events.Select(e => Math.Round(e.Seconds, 1)));
    }

    [Fact]
    public void GroupsOlderClipsByGameAndGaps()
    {
        var t = new DateTime(2026, 10, 20, 18, 0, 0);
        var clips = new[]
        {
            Clip("Overwatch", t, 30),
            Clip("Overwatch", t.AddMinutes(10), 30),
            Clip("Overwatch", t.AddMinutes(60), 30),  // more than 20 minutes later: a new session
            Clip("The Finals", t.AddMinutes(5), 30),
        };

        var groups = MatchGrouping.Group(clips);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Equal(0, g.Number));
        Assert.Equal([1, 1, 2], groups.Select(g => g.Clips.Count));
        Assert.Equal("Overwatch", groups[0].Game);
        Assert.Equal(t.AddMinutes(60).AddSeconds(-30), groups[0].Start);
    }

    [Fact]
    public void ReadsTheResultFromTheMatchEnd()
    {
        var metadata = Tagged("2026-10-27T20:00:00", 0, 1, ("kill", 3));
        metadata.Events.Add(new ClipEvent { Type = "match_end", Time = 9, Text = "VICTORY" });
        var group = MatchGrouping.Group([Clip("Battlefield 6", DateTime.Now, 10, metadata)]).Single();
        Assert.Equal("VICTORY", group.Result);
    }
}
