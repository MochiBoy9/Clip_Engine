using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class PlayerRecordsTests
{
    // A recording as src/app/Timeline.cpp writes it.
    private const string Valorant = """
        {"session": "2026-10-20T20:00:00", "game": "Valorant", "version": 1}
        {"t": 10.00, "match": 1, "type": "match_start", "text": "MATCH START"}
        {"t": 30.00, "match": 1, "type": "kill", "text": ""}
        {"t": 34.00, "match": 1, "type": "kill", "text": ""}
        {"t": 37.00, "match": 1, "type": "kill", "text": ""}
        {"t": 90.00, "match": 1, "type": "death", "text": ""}
        {"t": 120.00, "match": 1, "type": "kill", "text": ""}
        {"t": 130.00, "match": 1, "source": "screen", "stats": {"kills": 9, "deaths": 2, "assists": 5, "score": 280}}
        {"t": 140.00, "match": 1, "source": "screen", "stats": {"kills": 8, "deaths": 2, "assists": 5}}
        {"t": 300.00, "match": 1, "type": "match_end", "text": "VICTORY"}
        {"t": 400.00, "match": 2, "type": "match_start", "text": "MATCH START"}
        {"t": 420.00, "match": 2, "type": "kill", "text": ""}
        {"t": 500.00, "match": 2, "type": "death", "text": ""}
        {"t": 600.00, "match": 2, "type": "match_end", "text": "DEFEAT"}
        {"t": 610.00, "match": 3, "type": "kill", "t
        """;

    private static Timeline Parse(string text) => Timeline.Parse(text.Split('\n'))!;

    [Fact]
    public void ReadsATimelineAndSkipsALineCutShort()
    {
        var timeline = Parse(Valorant);
        Assert.Equal(("2026-10-20T20:00:00", "Valorant"), (timeline.Session, timeline.Game));
        Assert.Equal(new DateTime(2026, 10, 20, 20, 0, 0), timeline.Start);
        Assert.Equal(11, timeline.Moments.Count);
        Assert.Equal(2, timeline.Scores.Count);
        Assert.Equal(280, timeline.Scores[0].Stats["score"]);
        Assert.Null(Timeline.Parse(["{\"t\": 1}"]));  // not a timeline
    }

    [Fact]
    public void CountsEachMatchTakingTheScoreboardWhereItsHigher()
    {
        var book = PlayerRecords.Build([Parse(Valorant)], []);
        Assert.Equal(2, book.Matches.Count);
        var first = book.Matches[1];  // newest first
        // 4 kills seen, but the scoreboard said 9 (and a later misread of 8 doesn't lower it).
        Assert.Equal((9, 2, 5), (first.Kills, first.Deaths, first.Assists));
        Assert.True(first.FromScoreboard);
        Assert.True(first.Won);
        Assert.Equal(3, first.BestStreak);
        Assert.Equal(3, first.BestBurst);  // three kills within 10 seconds
        Assert.Equal(280, first.Extra["score"]);
        Assert.Equal(new DateTime(2026, 10, 20, 20, 0, 10), first.Start);
        Assert.True(book.Matches[0].Lost);
        Assert.Equal(10, book.Kills);
    }

    [Fact]
    public void FindsPersonalBests()
    {
        var book = PlayerRecords.Build([Parse(Valorant)], []);
        var kills = book.Find("kills")!;
        Assert.Equal((9, "Valorant", "9 kills"), (kills.Value, kills.Game, kills.ValueText));
        Assert.Equal(3, book.Find("streak")!.Value);
        Assert.Equal(3, book.Find("burst")!.Value);
        Assert.Equal(10, book.Find("day")!.Value);
        Assert.Null(book.Find("wins"));  // one win isn't a streak
        Assert.Equal(9, book.BestKills("valorant"));
        Assert.Equal(0, book.BestKills("Overwatch"));

        var totals = Assert.Single(book.Games);
        Assert.Equal(("Valorant", 2, 1, 10, 9), (totals.Game, totals.Matches, totals.Wins, totals.Kills, totals.BestKills));
    }

    [Fact]
    public void UsesClipsFromRecordingsWithoutATimeline()
    {
        var timeline = Parse(Valorant);
        ClipFile Clip(string session, int match, params (string Type, double Time)[] events) =>
            new($@"C:\clips\{session}.mp4", "Valorant", DateTime.Parse(session).AddMinutes(5), 1, TimeSpan.FromSeconds(30),
                new ClipMetadata
                {
                    Game = "Valorant", Session = session, SessionSeconds = 60, Match = match,
                    Events = events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time }).ToList(),
                });

        var book = PlayerRecords.Build([timeline],
        [
            // Already counted through its timeline.
            Clip("2026-10-20T20:00:00", 1, ("kill", 3), ("kill", 5)),
            // An older recording: its clip's moments are all there is.
            Clip("2026-10-01T18:00:00", 1, ("kill", 3), ("kill", 6), ("death", 20)),
        ]);
        Assert.Equal(3, book.Matches.Count);
        Assert.Equal((2, 1), (book.Matches[^1].Kills, book.Matches[^1].Deaths));
        Assert.Equal(12, book.Kills);
    }

    [Fact]
    public void WinStreaksSpanMatches()
    {
        var lines = new List<string> { """{"session": "2026-10-21T20:00:00", "game": "Overwatch", "version": 1}""" };
        var results = new[] { "VICTORY", "VICTORY", "DEFEAT", "VICTORY", "VICTORY", "VICTORY" };
        for (var i = 0; i < results.Length; i++)
        {
            lines.Add($$"""{"t": {{i * 100 + 1}}, "match": {{i + 1}}, "type": "kill", "text": ""}""");
            lines.Add($$"""{"t": {{i * 100 + 50}}, "match": {{i + 1}}, "type": "match_end", "text": "{{results[i]}}"}""");
        }
        var book = PlayerRecords.Build([Timeline.Parse(lines)!], []);
        Assert.Equal(3, book.Find("wins")!.Value);
        Assert.Equal(5, book.Wins);
    }
}
