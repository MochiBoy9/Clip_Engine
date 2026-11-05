using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class JournalTests
{
    // An evening of ARC Raiders as src/app/Timeline.cpp writes it: two raids and a queue backed out of.
    private const string ArcRaiders = """
        {"session": "2026-11-01T20:00:00", "game": "ARC Raiders", "version": 1}
        {"t": 10.00, "match": 0, "note": "map", "value": "Dam Battlegrounds", "text": "DAM BATTLEGROUNDS"}
        {"t": 12.00, "match": 0, "note": "condition", "value": "Night Raid", "text": "NIGHT RAID"}
        {"t": 20.00, "match": 0, "note": "inventory", "value": "Ferro", "text": "FERRO"}
        {"t": 20.50, "match": 0, "note": "inventory", "value": "Bandage", "amount": 3, "text": "BANDAGE x3"}
        {"t": 21.00, "match": 0, "note": "inventory", "value": "Bandage", "amount": 3, "text": "BANDAGE x3"}
        {"t": 30.00, "match": 0, "note": "queue", "text": "SEARCHING"}
        {"t": 60.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
        {"t": 120.00, "match": 0, "note": "enemy", "value": "Wasp", "xp": 50, "text": "WASP DESTROYED"}
        {"t": 150.00, "match": 0, "note": "enemy", "value": "Wasp", "xp": 50, "text": "WASP DESTROYED"}
        {"t": 200.00, "match": 0, "note": "enemy", "value": "Leaper", "xp": 400, "text": "LEAPER DESTROYED"}
        {"t": 250.00, "match": 0, "note": "loot", "value": "Metal Parts", "amount": 2, "text": "METAL PARTS x2"}
        {"t": 900.00, "match": 0, "note": "died", "text": "YOU DIED"}
        {"t": 920.00, "match": 0, "note": "xp", "amount": 1500, "text": "TOTAL XP 1,500"}
        {"t": 1000.00, "match": 0, "note": "map", "value": "Buried City", "text": "BURIED CITY"}
        {"t": 1010.00, "match": 0, "note": "queue", "text": "SEARCHING"}
        {"t": 1040.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
        {"t": 1100.00, "match": 0, "note": "enemy", "value": "Hornet", "text": "HORNET DESTROYED"}
        {"t": 1700.00, "match": 0, "note": "extracted", "text": "EXTRACTED"}
        {"t": 2000.00, "match": 0, "note": "queue", "text": "SEARCHING"}
        """;

    private static Timeline Parse(string text) => Timeline.Parse(text.Split('\n'))!;

    [Fact]
    public void ReadsJournalNotesFromATimeline()
    {
        var timeline = Parse(ArcRaiders);
        Assert.Equal(19, timeline.Notes.Count);
        Assert.Empty(timeline.Moments);
        var bandage = timeline.Notes[3];
        Assert.Equal(("inventory", "Bandage", 3, "BANDAGE x3"), (bandage.Note, bandage.Value, bandage.Amount, bandage.Text));
        Assert.Equal(50, timeline.Notes[7].Xp);
        Assert.Equal(1500, timeline.Notes[12].Amount);
    }

    [Fact]
    public void TellsTheRunsApart()
    {
        var game = Assert.Single(Journal.Build([Parse(ArcRaiders)]).Games);
        Assert.Equal("ARC Raiders", game.Game);
        Assert.Equal(3, game.Runs.Count);  // newest first

        var backedOut = game.Runs[0];
        Assert.Equal(RunOutcome.NotEntered, backedOut.Outcome);
        Assert.Null(backedOut.Duration);

        var extracted = game.Runs[1];
        Assert.Equal(RunOutcome.Extracted, extracted.Outcome);
        Assert.Equal("Buried City", extracted.Map);
        Assert.Equal(TimeSpan.FromSeconds(660), extracted.Duration);
        Assert.Empty(extracted.Lost);
        Assert.Empty(extracted.Conditions);  // the night raid was the first map's

        var died = game.Runs[2];
        Assert.Equal(RunOutcome.Died, died.Outcome);
        Assert.Equal(new DateTime(2026, 11, 1, 20, 1, 0), died.Start);
        Assert.Equal(TimeSpan.FromSeconds(840), died.Duration);
        Assert.False(died.DurationApproximate);
        Assert.Equal("Dam Battlegrounds", died.Map);
        Assert.Equal(new[] { "Night Raid" }, died.Conditions);
        Assert.Equal(new[] { new JournalEnemy("Wasp", 2, 100), new JournalEnemy("Leaper", 1, 400) }, died.Enemies);
        // The bandages were read twice in one look at the inventory: one stack, not two.
        Assert.Equal(new[] { new JournalItem("Ferro", 1), new JournalItem("Bandage", 3) }, died.Loadout);
        Assert.Equal(new[] { new JournalItem("Metal Parts", 2) }, died.Loot);
        // Dying loses everything taken in and picked up.
        Assert.Equal(new[] { new JournalItem("Ferro", 1), new JournalItem("Bandage", 3), new JournalItem("Metal Parts", 2) }, died.Lost);
        // The results screen just after counts for the raid that ended, ahead of the kill lines.
        Assert.Equal(1500, died.Xp);
    }

    [Fact]
    public void KeepsRecords()
    {
        var game = Journal.Build([Parse(ArcRaiders)]).Games[0];
        Assert.Equal("Buried City", game.Shortest!.Map);
        Assert.Equal("Dam Battlegrounds", game.Longest!.Map);
        Assert.Equal("Dam Battlegrounds", game.MostEnemies!.Map);
        Assert.Equal(1, game.BestExtractionStreak);
        Assert.Equal((2, 1, 1, 1), (game.Entered, game.Extracted, game.Died, game.NotEntered));
        Assert.Equal(0.5, game.ExtractionRate);
        Assert.Equal((4, 6), (game.EnemyCount, game.ItemsLost));
        Assert.Equal(new[] { "Buried City", "Dam Battlegrounds" }, game.Maps.Select(m => m.Name));
        Assert.Equal(new JournalEnemy("Wasp", 2, 100), game.EnemyTypes[0]);
        Assert.Contains("Your longest raid yet.", game.Longest.Highlights);
        Assert.Contains("Your shortest raid on record.", game.Shortest.Highlights);
    }

    [Fact]
    public void TellsTheStoryInTheGamesWords()
    {
        var game = Journal.Build([Parse(ArcRaiders)]).Games[0];
        var died = Journal.Story(game.Runs[2]);
        Assert.StartsWith("Went into Dam Battlegrounds (Night Raid). Took in Ferro and Bandage ×3. Destroyed 2 Wasps and a Leaper.", died);
        Assert.Contains("Picked up 2 items: Metal Parts ×2.", died);
        Assert.Contains("Died 14:00 in, losing Ferro, Bandage ×3 and Metal Parts ×2.", died);
        Assert.Contains("The most ARC you've destroyed in one raid.", died);

        Assert.StartsWith("Went into Buried City. Destroyed a Hornet. Extracted after 11:00.", Journal.Story(game.Runs[1]));
        Assert.Equal("Queued for a raid but didn't go in.", Journal.Story(game.Runs[0]));
    }

    [Fact]
    public void AddsThePlayerKillsLoggedByHand()
    {
        var id = "ARC Raiders|2026-11-01T20:00:00|60";
        var game = Journal.Build([Parse(ArcRaiders)], new Dictionary<string, int> { [id] = 2 }).Games[0];
        var died = game.Runs[2];
        Assert.Equal(id, died.Id);
        Assert.Equal(2, died.PlayerKills);
        Assert.Contains("Downed 2 raiders.", Journal.Story(died));
        Assert.Equal(2, game.PlayerKills);
        // Taking away more than was noted never goes below none.
        Assert.Equal(0, Journal.Build([Parse(ArcRaiders)], new Dictionary<string, int> { [id] = -3 }).Games[0].Runs[2].PlayerKills);
    }

    [Fact]
    public void ARunWhoseStartWasntSeenIsTimedFromTheFirstThingInIt()
    {
        var timeline = Parse("""
            {"session": "2026-11-02T18:00:00", "game": "Escape from Tarkov", "version": 1}
            {"t": 100.00, "match": 0, "note": "player_kill", "text": "KILLED"}
            {"t": 400.00, "match": 0, "note": "died", "text": "KILLED IN ACTION"}
            {"t": 410.00, "match": 0, "note": "lost", "value": "AK-74N", "text": "AK-74N"}
            {"t": 500.00, "match": 0, "note": "extracted", "text": "SURVIVED"}
            """);
        var run = Assert.Single(Journal.Build([timeline]).Games[0].Runs);  // the second result is the same screen
        Assert.Equal(RunOutcome.Died, run.Outcome);
        Assert.True(run.DurationApproximate);
        Assert.Equal(TimeSpan.FromSeconds(300), run.Duration);
        Assert.Equal(new[] { new JournalItem("AK-74N", 1) }, run.Lost);  // what the game said was lost
        Assert.StartsWith("Started a raid. Killed a player. Died at least 5:00 in, losing AK-74N.", Journal.Story(run));
    }

    [Fact]
    public void HuntMatchesFromTheGamesOwnSummary()
    {
        var timeline = Parse("""
            {"session": "2026-11-03T21:00:00", "game": "Hunt: Showdown 1896", "version": 1}
            {"t": 1500.00, "match": 0, "note": "deploy", "value": "Bounty Hunt", "text": ""}
            {"t": 1500.00, "match": 0, "note": "enemy", "value": "Monster", "amount": 12, "text": ""}
            {"t": 1500.00, "match": 0, "note": "enemy", "value": "Boss", "amount": 1, "text": ""}
            {"t": 1500.00, "match": 0, "note": "player_kill", "amount": 3, "text": ""}
            {"t": 1500.00, "match": 0, "note": "xp", "amount": 2400, "text": ""}
            {"t": 1500.00, "match": 0, "note": "extracted", "text": ""}
            """);
        var run = Assert.Single(Journal.Build([timeline]).Games[0].Runs);
        Assert.Equal((RunOutcome.Extracted, "Bounty Hunt", 13, 3, 2400), (run.Outcome, run.Mission, run.EnemyCount, run.PlayerKills, run.Xp));
        Assert.Null(run.Duration);  // the summary is written all at once, so the match can't be timed
        Assert.StartsWith("Went into Bounty Hunt. Killed 12 Monsters and a Boss. Took down 3 hunters. Extracted.", Journal.Story(run));
    }

    [Fact]
    public void XpOnTheResultsLineCountsForTheRunThatEnded()
    {
        var timeline = Parse("""
            {"session": "2026-11-02T18:00:00", "game": "Deep Rock Galactic", "version": 1}
            {"t": 10.00, "match": 0, "note": "deploy", "text": "DROPPING"}
            {"t": 60.00, "match": 0, "note": "enemy", "value": "Glyphid", "xp": 10, "text": "GLYPHID +10 XP"}
            {"t": 600.00, "match": 0, "note": "extracted", "xp": 900, "text": "MISSION COMPLETE +900 XP"}
            {"t": 605.00, "match": 0, "note": "objective", "xp": 200, "text": "SECONDARY +200 XP"}
            """);
        var run = Assert.Single(Journal.Build([timeline]).Games[0].Runs);
        Assert.Equal(1110, run.Xp);
    }

    [Fact]
    public void TheResultsScreenEndsARunWhoseEndingWasMissed()
    {
        // No extraction banner was read, and the results screen is read a minute later, counting up as it opens.
        var timeline = Parse("""
            {"session": "2026-11-04T19:00:00", "game": "ARC Raiders", "version": 1}
            {"t": 10.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 100.00, "match": 0, "note": "enemy", "value": "Wasp", "xp": 100, "text": "DESTROYED WASP 100 XP"}
            {"t": 700.00, "match": 0, "note": "enemy", "value": "Arc", "text": "DESTROYED ARC 1 100"}
            {"t": 701.00, "match": 0, "note": "xp", "amount": 340, "text": "SCAVENGED 340"}
            {"t": 702.00, "match": 0, "note": "xp", "amount": 2450, "text": "SCAVENGED 2 450"}
            {"t": 702.00, "match": 0, "note": "xp", "amount": 1100, "text": "DESTROYED ARC 1 100"}
            {"t": 702.50, "match": 0, "note": "extracted", "text": "SAFE RETURN BONUS 1 500"}
            {"t": 703.00, "match": 0, "note": "xp", "amount": 1500, "text": "SAFE RETURN BONUS 1 500"}
            {"t": 760.00, "match": 0, "note": "enemy", "value": "Arc", "text": "DESTROYED ARC 1 100"}
            {"t": 761.00, "match": 0, "note": "xp", "amount": 2450, "text": "SCAVENGED 2 450"}
            {"t": 900.00, "match": 0, "note": "queue", "text": "SEARCHING"}
            {"t": 960.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 1000.00, "match": 0, "note": "enemy", "value": "Tick", "text": "DESTROYED TICK"}
            """);
        var runs = Journal.Build([timeline]).Games[0].Runs;
        Assert.Equal(2, runs.Count);  // the results screen read again isn't another raid
        var raid = runs[1];
        Assert.Equal(RunOutcome.Extracted, raid.Outcome);
        // Each line at the most it showed, once: 2,450 + 1,100 + 1,500.
        Assert.Equal(5050, raid.Xp);
        Assert.Equal(TimeSpan.FromSeconds(691), raid.Duration);
        // The summary's line for every ARC destroyed is read as one more just before the screen is known for what it is.
        Assert.Equal(2, raid.EnemyCount);
        Assert.Equal(new[] { new JournalEnemy("Tick", 1, 0) }, runs[0].Enemies);
    }

    [Fact]
    public void TheResultsTotalIsTheRunsXp()
    {
        var timeline = Parse("""
            {"session": "2026-11-04T19:00:00", "game": "Escape from Tarkov", "version": 1}
            {"t": 10.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 600.00, "match": 0, "note": "extracted", "text": "SURVIVED"}
            {"t": 610.00, "match": 0, "note": "xp", "amount": 4200, "text": "EXPERIENCE GAINED 4 200"}
            {"t": 610.00, "match": 0, "note": "xp", "amount": 5100, "text": "TOTAL EXPERIENCE 5 100"}
            """);
        Assert.Equal(5100, Assert.Single(Journal.Build([timeline]).Games[0].Runs).Xp);
    }

    [Fact]
    public void AKillTallyOnTheResultsCountsForTheRunThatEnded()
    {
        var timeline = Parse("""
            {"session": "2026-11-04T19:00:00", "game": "Helldivers 2", "version": 1}
            {"t": 10.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 1500.00, "match": 0, "note": "extracted", "text": "MISSION COMPLETE"}
            {"t": 1560.00, "match": 0, "note": "enemy", "amount": 212, "text": "KILLS 212"}
            """);
        var run = Assert.Single(Journal.Build([timeline]).Games[0].Runs);
        Assert.Equal((RunOutcome.Extracted, 212), (run.Outcome, run.EnemyCount));
    }

    [Fact]
    public void TiesRunsToTheirClipsAndTalliesItems()
    {
        var day = new DateTime(2026, 11, 1, 20, 0, 0);
        ClipFile Clip(string name, int seconds) => new($@"C:\clips\{name}.mp4", "ARC Raiders", day.AddSeconds(seconds), 1, null);
        var clips = new[] { Clip("lobby", 30), Clip("wasp", 125), Clip("leaper", 205), Clip("hornet", 1105), Clip("late", 1800) };
        var game = Journal.Build([Parse(ArcRaiders)], clips: clips).Games[0];
        Assert.Equal(new[] { "wasp", "leaper" }, game.Runs[2].Clips.Select(c => Path.GetFileNameWithoutExtension(c.Path)));
        Assert.Equal(new[] { "hornet" }, game.Runs[1].Clips.Select(c => Path.GetFileNameWithoutExtension(c.Path)));
        Assert.Equal(new JournalItem("Bandage", 3), game.MostLost[0]);
        Assert.Equal(new[] { new JournalItem("Metal Parts", 2) }, game.MostFound);
    }

    [Fact]
    public void AMapsExtractionRateIsOutOfTheRunsThatEnded()
    {
        var timeline = Parse("""
            {"session": "2026-11-02T18:00:00", "game": "ARC Raiders", "version": 1}
            {"t": 10.00, "match": 0, "note": "map", "value": "Spaceport", "text": "SPACEPORT"}
            {"t": 20.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 300.00, "match": 0, "note": "died", "text": "YOU DIED"}
            {"t": 600.00, "match": 0, "note": "map", "value": "Spaceport", "text": "SPACEPORT"}
            {"t": 620.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 900.00, "match": 0, "note": "extracted", "text": "EXTRACTED"}
            {"t": 1000.00, "match": 0, "note": "map", "value": "Spaceport", "text": "SPACEPORT"}
            {"t": 1020.00, "match": 0, "note": "deploy", "text": "DEPLOYING"}
            {"t": 1100.00, "match": 0, "note": "enemy", "value": "Wasp", "text": "WASP"}
            """);
        var game = Journal.Build([timeline]).Games[0];
        var map = Assert.Single(game.Maps);
        Assert.Equal((3, 1, 2), (map.Runs, map.Extracted, map.Ended));  // the last raid's end wasn't recorded
        Assert.Equal(0.5, map.ExtractionRate);
        Assert.Contains("Your first time making it out of Spaceport, after 1 try.", game.Runs[1].Highlights);
    }

    [Fact]
    public void ATimelineWithoutNotesHasNoRuns()
    {
        var timeline = Parse("""
            {"session": "2026-11-02T18:00:00", "game": "Valorant", "version": 1}
            {"t": 10.00, "match": 1, "type": "kill", "text": ""}
            """);
        Assert.Empty(Journal.Build([timeline]).Games);
    }

    [Fact]
    public void ReadsTheEnginesJournalLines()
    {
        var line = EngineLogParser.ParseLine("2026-11-01 20:15:00.000 INFO  Journal enemy in ARC Raiders: Wasp (2)");
        var noted = Assert.IsType<JournalNoted>(EngineLogParser.ParseEvent(line!));
        Assert.Equal(("enemy", "ARC Raiders", "Wasp (2)"), (noted.Note, noted.Game, noted.Value));
        Assert.Equal("Extracted", JournalNoted.Describe("extracted"));
        Assert.Equal("Player kill", JournalNoted.Describe("player_kill"));
    }

    [Theory]
    [InlineData(3, "Snitch", "3 Snitches")]
    [InlineData(2, "Boss", "2 Bosses")]
    [InlineData(1, "Boss", "a Boss")]
    [InlineData(1, "Metal Parts", "1 Metal Parts")]
    [InlineData(1, "ARC Probe", "an ARC Probe")]
    [InlineData(4, "Tick", "4 Ticks")]
    public void CountsThingsInPlainEnglish(int count, string name, string expected) =>
        Assert.Equal(expected, Journal.Counted(count, name));

    [Theory]
    [InlineData("arc raiders", "raid", "ARC")]
    [InlineData("Deep Rock Galactic", "mission", "creatures")]
    [InlineData("Some Other Game", "run", "enemies")]
    public void SpeaksEachGamesLanguage(string game, string run, string enemies)
    {
        var words = JournalWords.For(game);
        Assert.Equal((run, enemies), (words.Run, words.Enemies));
    }
}
